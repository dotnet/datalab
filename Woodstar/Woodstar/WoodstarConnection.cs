using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Woodstar.Tds;

namespace Woodstar;

// Implementation
public sealed partial class WoodstarConnection
{
    WoodstarDataSource? _dataSource;
    OperationSlot? _operationSlot;
    ConnectionState _state;
    Exception? _breakException;
    bool _disposed;
    string _connectionString;

    // Slots are thread safe up to the granularity of the slot, anything more is the responsibility of the caller.
    volatile SemaphoreSlim? _pipeliningWriteLock;
    volatile ConnectionOperationSource? _pipelineHead;
    volatile ConnectionOperationSource? _pipelineTail;
    ConnectionOperationSource? _operationSingleton;

    internal WoodstarDataSource DbDataSource
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (_dataSource is null && _connectionString is "")
                throw new InvalidOperationException($"{nameof(DbDataSource)} cannot be resolved, {nameof(ConnectionString)} is not set.");

            return _dataSource ??= ChangeDataSource(_connectionString);
        }
    }

    void ThrowIfDisposed()
    {
        if (_disposed)
            ThrowObjectDisposed();

        static void ThrowObjectDisposed() => throw new ObjectDisposedException(nameof(WoodstarConnection));
    }

    OperationSlot GetSlotUnsynchronized()
    {
        ThrowIfNoSlot();
        return _operationSlot!;
    }

    [MemberNotNullWhen(true, nameof(SyncObj), nameof(_operationSlot))]
    bool HasSlot => _operationSlot is not null;

    [MemberNotNull(nameof(SyncObj), nameof(_operationSlot))]
    void ThrowIfNoSlot()
    {
        Debug.Assert(SyncObj is not null && _operationSlot is not null);
        if (!HasSlot || _disposed || _state is ConnectionState.Broken or not (ConnectionState.Open or ConnectionState.Executing or ConnectionState.Fetching))
            HandleUncommon();

        void HandleUncommon()
        {
            ThrowIfDisposed();

            if (_state is ConnectionState.Broken)
                throw new InvalidOperationException("Connection is in a broken state.", _breakException);

            if (_state is not (ConnectionState.Open or ConnectionState.Executing or ConnectionState.Fetching))
                throw new InvalidOperationException("Connection is not open or ready.");
        }
    }

    internal bool ConnectionOpInProgress
        => _pipelineTail is { IsCompleted: false };

    void MoveToConnecting()
    {
        if (_state is not (ConnectionState.Closed or ConnectionState.Broken))
            throw new InvalidOperationException("Connection is already open or being opened.");

        _state = ConnectionState.Connecting;
    }

    void MoveToExecuting()
    {
        Debug.Assert(_state is not (ConnectionState.Closed or ConnectionState.Broken), "Already Closed or Broken.");
        Debug.Assert(_state is ConnectionState.Open or ConnectionState.Executing or ConnectionState.Fetching, "Called on an unopened/not fetching/not executing connection.");
        // We allow pipelining so we can be both fetching and executing, in such case leave fetching in place.
        if (_state != ConnectionState.Fetching)
            _state = ConnectionState.Executing;
    }

    void MoveToFetching()
    {
        Debug.Assert(_state is not (ConnectionState.Closed or ConnectionState.Broken), "Already Closed or Broken.");
        Debug.Assert(_state is ConnectionState.Open or ConnectionState.Executing or ConnectionState.Fetching, "Called on an unopened/not fetching/not executing connection.");
        _state = ConnectionState.Fetching;
    }

    void EndSlot(OperationSlot slot)
    {
        Debug.Assert(slot.Task.IsCompletedSuccessfully);
        slot.Task.Result.Complete(_breakException);
    }

    void MoveToBroken(Exception? exception = null, ConnectionOperationSource? pendingHead = null)
    {
        ThrowIfNoSlot();

        OperationSlot slot;
        lock (SyncObj)
        {
            slot = _operationSlot;
            // We'll just keep the first exception.
            if (_state is ConnectionState.Broken)
                return;

            _state = ConnectionState.Broken;
            _breakException = exception;
        }

        var next = pendingHead;
        while (next is not null)
        {
            next.TryComplete(exception);
            next = next.Next;
        }

        EndSlot(slot);
    }

    async ValueTask CloseCore(bool async, Exception? exception = null)
    {
        // The only time SyncObj (_operationSlot) is null, before the first successful open.
        if (!HasSlot)
            return;

        OperationSlot slot;
        Task drainingTask;
        lock (SyncObj)
        {
            // We don't use GetSlotUnsynchronized as it checks for _disposed.
            slot = _operationSlot;
            // Only throw if we're already closed and disposed.
            if (_state is ConnectionState.Closed)
            {
                ThrowIfDisposed();
                return;
            }

            if (_operationSlot.IsCompleted && _pipelineTail is { IsCompleted: false })
            {
                // If we still have pending operations while the connection slot is completed we need to forcibly complete the first which should complete the rest.
                _pipelineHead?.TryComplete(exception);
                drainingTask = Task.CompletedTask;
            }
            else if (_pipelineTail is null || _pipelineTail.IsCompleted)
                drainingTask = Task.CompletedTask;
            else
            {
                drainingTask = CreateSlotUnsynchronized(slot, true).Task.AsTask();
            }
        }

        // TODO, if somebody pipelines without reading and then Closes we'll wait forever for the commands to finish as their readers won't get disposed.
        // Probably want a timeout and then force complete them like in broken.
        // This is important for pool starvation as well as contrary to npgsql we *do* schedule exclusive use operations onto pending/active exclusive use connections.
        if (async)
            await drainingTask.ConfigureAwait(false);
        else
            // TODO we may want a latch to prevent sync and async capabilities (pipelining) mixing like this.
            // Best we can do, this will only happen if somebody closes synchronously while having executed commands asynchronously.
            drainingTask.Wait();
        EndSlot(slot);
        Reset();
    }

    void MoveToIdle()
    {
        // First open.
        if (!HasSlot)
        {
            _state = ConnectionState.Open;
            return;
        }

        lock (SyncObj)
        {
            // No debug assert as completion can often happen in finally blocks, just check here.
            if (_state is not (ConnectionState.Closed or ConnectionState.Broken))
                _state = ConnectionState.Open;

            _pipelineHead = null;
            _pipelineTail = null;
        }
    }

    void Reset()
    {
        _operationSingleton = null;
        _pipelineHead = null;
        _pipelineTail = null;
        _state = default;
        _breakException = null;
    }

    object? SyncObj => _operationSlot;

    internal void PerformUserCancellation()
    {
        ThrowIfNoSlot();

        var start = Environment.TickCount64;
        Tds.Protocol? protocol;
        SemaphoreSlim? writeLock;
        lock (SyncObj)
        {
            var connectionSlot = GetSlotUnsynchronized();
            protocol = connectionSlot.Protocol;
            if (protocol is null)
                return;
            if (_pipeliningWriteLock is null)
                writeLock = _pipeliningWriteLock = new SemaphoreSlim(0); // init with empty count.
            else if (_pipeliningWriteLock.Wait(DbDataSource.DefaultCancellationTimeout))
                writeLock = _pipeliningWriteLock;
            else
                writeLock = null;
        }

        // We timed out before getting the lock, tear down the connection (highly undesirable).
        if (writeLock is null)
            // TODO or something like this.
            Task.Run(() => protocol.CompleteAsync(new Exception("Connection was prematurely completed as a result of a user cancellation fallback tearing down the socket.")));

        try
        {
            var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - start);
            DbDataSource.PerformUserCancellation(protocol, DbDataSource.DefaultCancellationTimeout - elapsed);
        }
        finally
        {
            _pipeliningWriteLock.Release();
        }
    }

    internal readonly struct CommandWriter
    {
        readonly WoodstarConnection _instance;

        public CommandWriter(WoodstarConnection instance)
        {
            _instance = instance;
        }

        public CommandContextBatch WriteCommand<TCommand>(bool allowPipelining, TCommand command, bool closeConnection, CancellationToken cancellationToken = default)
            where TCommand : ISqlCommand
        {
            _instance.ThrowIfNoSlot();

            OperationSlot slot;
            ConnectionOperationSource subSlot;
            lock (_instance.SyncObj)
            {
                slot = _instance.GetSlotUnsynchronized();
                // Connection open invariant is that we can already begin reading on our slot, otherwise we'd need to tie the tasks together.
                Debug.Assert(slot.Task.IsCompletedSuccessfully);
                // First enqueue, only then call WriteCommandCore.
                subSlot = EnqueueReadUnsynchronized(slot, allowPipelining, closeConnection);
            }

            BeginWrite(async: false, cancellationToken: cancellationToken).GetAwaiter().GetResult();
            ValueTask<WriteResult> writeTask = new ValueTask<WriteResult>();
            try
            {
                _instance.MoveToExecuting();
                var result = _instance.DbDataSource.WriteCommand(slot, command);
                writeTask = result.Single.WriteTask;
                return result.Single.WithIOCompletionPair(new IOCompletionPair(writeTask, subSlot));
            }
            finally
            {
                // This will usually finish synchronously, but we can't await it, otherwise we're awaiting the entire write before we can start reading.
                var _ = EndWrite(writeTask);
            }
        }

        // This will almost always complete synchronously, but if it won't the chances a pooled instance isn't available is almost unthinkable.
#if !NETSTANDARD2_0
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
        public async ValueTask<CommandContextBatch> WriteCommandAsync<TCommand>(bool allowPipelining, TCommand command, bool closeConnection, CancellationToken cancellationToken = default)
            where TCommand : ISqlCommand
        {
            _instance.ThrowIfNoSlot();

            OperationSlot slot;
            ConnectionOperationSource subSlot;
            lock (_instance.SyncObj)
            {
                slot = _instance.GetSlotUnsynchronized();
                // Connection open invariant is that we can already begin reading on our slot, otherwise we'd need to tie the tasks together.
                Debug.Assert(slot.Task.IsCompletedSuccessfully);
                // First enqueue, only then call WriteCommandCore.
                subSlot = EnqueueReadUnsynchronized(slot, allowPipelining, closeConnection);
            }

            await BeginWrite(async: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            ValueTask<WriteResult> writeTask = new ValueTask<WriteResult>();
            try
            {
                _instance.MoveToExecuting();
                var result = await _instance.DbDataSource.WriteCommandAsync(slot, command, cancellationToken);
                writeTask = result.Single.WriteTask;
                return result.Single.WithIOCompletionPair(new IOCompletionPair(writeTask, subSlot));
            }
            finally
            {
                // This will usually finish synchronously, but we can't await it, otherwise we're awaiting the entire write before we can start reading.
                var _ = EndWrite(writeTask);
            }
        }

        ConnectionOperationSource EnqueueReadUnsynchronized(OperationSlot connectionSlot, bool allowPipelining, bool closeConnection)
        {
            var current = _instance._pipelineTail;
            if (!allowPipelining && !(current is null || current.IsCompleted))
                ThrowCommandInProgress();

            if (closeConnection && current is not null && current.CloseConnection)
                ThrowConnectionWillClose();

            var source = _instance._pipelineTail = _instance.CreateSlotUnsynchronized(connectionSlot, closeConnection);
            if (_instance._pipelineHead is null)
                _instance._pipelineHead = source;
            // An immediately active read means head == tail, move to fetching immediately.
            if (source.Task.IsCompletedSuccessfully)
                _instance.MoveToFetching();

            return source;

            void ThrowCommandInProgress() => throw new InvalidOperationException("A command is already in progress.");
            void ThrowConnectionWillClose() => throw new InvalidOperationException("A previous command specified CommandBehavior.CloseConnection, no new commands can be executed.");
        }

        Task BeginWrite(bool async, TimeSpan timeout = default, CancellationToken cancellationToken = default)
        {
            var writeLock = _instance._pipeliningWriteLock;
            if (writeLock is null)
            {
                var value = new SemaphoreSlim(1);
#pragma warning disable CS0197
                writeLock = Interlocked.CompareExchange(ref _instance._pipeliningWriteLock, value, null);
#pragma warning restore CS0197
                if (writeLock is null)
                    writeLock = value;
            }

            if (async)
            {
                if (!writeLock.Wait(0))
                    return writeLock.WaitAsync(cancellationToken);

            }
            else
            {
                writeLock.Wait(timeout);
            }

            return Task.CompletedTask;
        }

#if !NETSTANDARD2_0
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
#endif
        async ValueTask EndWrite(ValueTask<WriteResult> writeTask)
        {
            try
            {
                await writeTask.ConfigureAwait(false);
            }
            catch
            {
                // TODO kill the connection.
                throw;
            }
            var writeLock = _instance._pipeliningWriteLock;
            if (writeLock?.CurrentCount == 0)
                writeLock.Release();
            else
                // TODO don't throw, break the connection with this reason.
                throw new InvalidOperationException("No write to end.");
        }
    }

    internal CommandWriter GetCommandWriter() => new(this);
    internal TimeSpan DefaultCommandTimeout => DbDataSource.DefaultCommandTimeout;

    ConnectionOperationSource CreateSlotUnsynchronized(OperationSlot connectionSlot, bool closeConnection)
    {
        Debug.Assert(!connectionSlot.IsCompleted);
        ConnectionOperationSource source;
        var current = _pipelineTail;
        if (current is null || current.IsCompleted)
        {
            var singleton = _operationSingleton;
            if (singleton is not null)
            {
                singleton.Reset();
                source = singleton;
            }
            else
            {
                Debug.Assert(connectionSlot.Protocol is not null);
                source = _operationSingleton = new ConnectionOperationSource(this, connectionSlot.Protocol, closeConnection, pooled: true);
            }
        }
        else
        {
            Debug.Assert(connectionSlot.Protocol is not null);
            source = new ConnectionOperationSource(this, connectionSlot.Protocol, closeConnection);
            current.Next = source;
        }

        return source;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void CompleteOperation(ConnectionOperationSource? next, Exception? exception, bool closeConnection)
    {
        // There should never be a next after closeConnection is true.
        Debug.Assert(!closeConnection || closeConnection && next is null);
        Debug.Assert(HasSlot);

        lock (SyncObj)
        {
            _pipelineHead = next;
        }
        if (exception is not null)
            MoveToBroken(exception, next);
        else if (closeConnection)
            // TODO We could do something here to make this async.
            Close();
        else if (next is null)
            MoveToIdle();
        else
            next.Activate();
    }

    sealed class ConnectionOperationSource: OperationSource, IValueTaskSource<Operation>
    {
        readonly WoodstarConnection _connection;
        ConnectionOperationSource? _next;
        Task<Operation>? _task;

        // Pipelining on the same connection is expected to be done on the same thread.
        public ConnectionOperationSource(WoodstarConnection connection, Tds.Protocol protocol, bool closeConnection, bool pooled = false) :
            base(protocol, pooled)
        {
            ValueTaskSource.RunContinuationsAsynchronously = false;
            CloseConnection = closeConnection;
            _connection = connection;
        }

        public bool CloseConnection { get; }
        public void Activate() => ActivateCore();

        public ConnectionOperationSource? Next
        {
            get => _next;
            set
            {
                if (Interlocked.CompareExchange(ref _next, value, null) is null)
                    return;

                throw new InvalidOperationException("Next was already set.");
            }
        }

        public override ValueTask<Operation> Task => new(_task ??= new ValueTask<Operation>(this, ValueTaskSource.Version).AsTask());

        protected override void CompleteCore(Tds.Protocol protocol, Exception? exception)
            => _connection.CompleteOperation(_next, exception, CloseConnection);

        protected override void ResetCore()
        {
            _next = null;
        }

        internal new void Reset() => base.Reset();
        Operation IValueTaskSource<Operation>.GetResult(short token) => ValueTaskSource.GetResult(token);
        ValueTaskSourceStatus IValueTaskSource<Operation>.GetStatus(short token) => ValueTaskSource.GetStatus(token);
        void IValueTaskSource<Operation>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => ValueTaskSource.OnCompleted(continuation, state, token, flags);
    }

    async ValueTask DisposeCore(bool async)
    {
        if (_disposed)
            return;
        _disposed = true;
        await CloseCore(async).ConfigureAwait(false);
        base.Dispose(true);
    }

    WoodstarTransaction BeginTransactionCore(IsolationLevel isolationLevel)
    {
        // TODO
        throw new NotImplementedException();
    }

    async Task OpenCore(bool async, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        MoveToConnecting();
        try
        {
            // First we get a slot (could be a connection open but usually this is synchronous)
            _operationSlot = async
                ? await DbDataSource.GetSlotAsync(exclusiveUse: true, DbDataSource.ConnectionTimeout, cancellationToken).ConfigureAwait(false)
                : DbDataSource.GetSlot(exclusiveUse: true, DbDataSource.ConnectionTimeout);
            if (!async)
                Debug.Assert(_operationSlot.Task.IsCompleted);
            // Then we await until the connection is fully ready for us (both tasks are covered by the same cancellationToken).
            // In non exclusive cases we already start writing our message as well but we choose not to do so here.
            // One of the reasons would be to be sure the connection is healthy once we transition to Open.
            // If we're still stuck in a pipeline we won't know for sure.
            await _operationSlot.Task.ConfigureAwait(false);
            MoveToIdle();
        }
        catch
        {
            await CloseCore(async: true).ConfigureAwait(false);
            throw;
        }
    }

    WoodstarDataSource ChangeDataSource(string connectionString)
    {
        ThrowIfDisposed();
        if (_state is not ConnectionState.Closed or ConnectionState.Broken)
            throw new InvalidOperationException("Cannot change connection string while the connection is open.");

        _dataSource = null;
        // TODO change the datasource etc.
        _connectionString = _dataSource!.ConnectionString;

        throw new NotImplementedException();
    }

    WoodstarConnection CloneCore()
    {
        if (_dataSource is not null)
            return _dataSource.CreateConnection();

        return new WoodstarConnection(_connectionString);
    }
}

// Public surface & ADO.NET
public sealed partial class WoodstarConnection : DbConnection, ICloneable
{
    /// <summary>
    /// Initializes a new instance of <see cref="WoodstarConnection"/> with the given connection string.
    /// </summary>
    /// <param name="connectionString">The connection used to open the PostgreSQL database.</param>
    public WoodstarConnection(string? connectionString)
    {
        GC.SuppressFinalize(this);
        _connectionString = connectionString ?? string.Empty;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WoodstarConnection"/> class.
    /// </summary>
    public WoodstarConnection() : this(connectionString: null) { }

    internal WoodstarConnection(WoodstarDataSource dataSource) : this(dataSource.ConnectionString)
        => _dataSource = dataSource;

    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionString;
        set => ChangeDataSource(value ?? string.Empty);
    }
    public override string Database => DbDataSource.Database;
    public override string DataSource => DbDataSource.EndPointRepresentation;
    public override int ConnectionTimeout => (int)DbDataSource.ConnectionTimeout.TotalSeconds;
    public override string ServerVersion => DbDataSource.ServerVersion;
    public override ConnectionState State => _state;

    public override void Open() => OpenCore(async: false, CancellationToken.None).GetAwaiter().GetResult();
    public override Task OpenAsync(CancellationToken cancellationToken) => OpenCore(async: true, cancellationToken);

    public override void ChangeDatabase(string databaseName)
    {
        // TODO actually update the databasename.
        var updatedConnectionString = DbDataSource.ConnectionString;
        CloseCore(async: false).GetAwaiter().GetResult();
        ChangeDataSource(updatedConnectionString);
        OpenCore(async: false, CancellationToken.None).GetAwaiter().GetResult();
    }

#if !NETSTANDARD2_0
    public override async Task ChangeDatabaseAsync(string databaseName, CancellationToken cancellationToken = default)
#else
    public async Task ChangeDatabaseAsync(string databaseName, CancellationToken cancellationToken = default)
#endif
    {
        // TODO actually update the databasename.
        var updatedConnectionString = DbDataSource.ConnectionString;
        await CloseCore(async: true);
        ChangeDataSource(updatedConnectionString);
        await OpenCore(async: true, cancellationToken);
    }

    /// <summary>
    /// Begins a database transaction.
    /// </summary>
    /// <returns>A <see cref="WoodstarTransaction"/> object representing the new transaction.</returns>
    /// <remarks>
    /// Nested transactions are not supported.
    /// Transactions created by this method will have the <see cref="IsolationLevel.ReadCommitted"/> isolation level.
    /// </remarks>
    public new WoodstarTransaction BeginTransaction()
        => BeginTransaction(IsolationLevel.Unspecified);

    /// <summary>
    /// Begins a database transaction with the specified isolation level.
    /// </summary>
    /// <param name="level">The isolation level under which the transaction should run.</param>
    /// <returns>A <see cref="WoodstarTransaction"/> object representing the new transaction.</returns>
    /// <remarks>Nested transactions are not supported.</remarks>
    public new WoodstarTransaction BeginTransaction(IsolationLevel level)
        => BeginTransactionCore(level);

    /// <summary>
    /// Begins a database transaction.
    /// </summary>
    /// <returns>A <see cref="WoodstarTransaction"/> object representing the new transaction.</returns>
    /// <remarks>
    /// Nested transactions are not supported.
    /// Transactions created by this method will have the <see cref="IsolationLevel.ReadCommitted"/> isolation level.
    /// </remarks>
#if !NETSTANDARD2_0
    public new ValueTask<DbTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
#else
    public ValueTask<DbTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
#endif
        => new(BeginTransactionCore(IsolationLevel.Unspecified));

    /// <summary>
    /// Begins a database transaction with the specified isolation level.
    /// </summary>
    /// <param name="level">The isolation level under which the transaction should run.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>A <see cref="WoodstarTransaction"/> object representing the new transaction.</returns>
    /// <remarks>Nested transactions are not supported.</remarks>
#if !NETSTANDARD2_0
    public new ValueTask<WoodstarTransaction> BeginTransactionAsync(IsolationLevel level, CancellationToken cancellationToken = default)
#else
    public ValueTask<WoodstarTransaction> BeginTransactionAsync(IsolationLevel level, CancellationToken cancellationToken = default)
#endif
        => new(BeginTransactionCore(IsolationLevel.Unspecified));

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => BeginTransactionCore(isolationLevel);

    protected override DbCommand CreateDbCommand() => new WoodstarCommand(null, this);

    public override void Close() => CloseCore(async: false).GetAwaiter().GetResult();

#if !NETSTANDARD2_0
    public override Task CloseAsync()
#else
    public Task CloseAsync()
#endif
        => CloseCore(async: true).AsTask();

#if !NETSTANDARD2_0
    public override ValueTask DisposeAsync()
#else
    public ValueTask DisposeAsync()
#endif
        => DisposeCore(async: true);

    protected override void Dispose(bool disposing)
        => DisposeCore(async: false).GetAwaiter().GetResult();

    public WoodstarConnection Clone() => CloneCore();
    object ICloneable.Clone() => CloneCore();

    /// <summary>
    /// Returns the schema collection specified by the collection name.
    /// </summary>
    /// <param name="collectionName">The collection name.</param>
    /// <returns>The collection specified.</returns>
    public override DataTable GetSchema(string? collectionName) => GetSchema(collectionName, null);

    /// <summary>
    /// Returns the schema collection specified by the collection name filtered by the restrictions.
    /// </summary>
    /// <param name="collectionName">The collection name.</param>
    /// <param name="restrictions">
    /// The restriction values to filter the results.  A description of the restrictions is contained
    /// in the Restrictions collection.
    /// </param>
    /// <returns>The collection specified.</returns>
    public override DataTable GetSchema(string? collectionName, string?[]? restrictions)
        => throw new NotImplementedException();

    /// <summary>
    /// Asynchronously returns the supported collections.
    /// </summary>
    /// <param name="cancellationToken">
    /// An optional token to cancel the asynchronous operation. The default value is <see cref="CancellationToken.None"/>.
    /// </param>
    /// <returns>The collection specified.</returns>
#if !NETSTANDARD2_0
    public override Task<DataTable> GetSchemaAsync(CancellationToken cancellationToken = default)
#else
    public Task<DataTable> GetSchemaAsync(CancellationToken cancellationToken = default)
#endif
        => GetSchemaAsync("MetaDataCollections", null, cancellationToken);

    /// <summary>
    /// Asynchronously returns the schema collection specified by the collection name.
    /// </summary>
    /// <param name="collectionName">The collection name.</param>
    /// <param name="cancellationToken">
    /// An optional token to cancel the asynchronous operation. The default value is <see cref="CancellationToken.None"/>.
    /// </param>
    /// <returns>The collection specified.</returns>
#if !NETSTANDARD2_0
    public override Task<DataTable> GetSchemaAsync(string collectionName, CancellationToken cancellationToken = default)
#else
    public Task<DataTable> GetSchemaAsync(string collectionName, CancellationToken cancellationToken = default)
#endif
        => GetSchemaAsync(collectionName, null, cancellationToken);

    /// <summary>
    /// Asynchronously returns the schema collection specified by the collection name filtered by the restrictions.
    /// </summary>
    /// <param name="collectionName">The collection name.</param>
    /// <param name="restrictions">
    /// The restriction values to filter the results.  A description of the restrictions is contained
    /// in the Restrictions collection.
    /// </param>
    /// <param name="cancellationToken">
    /// An optional token to cancel the asynchronous operation. The default value is <see cref="CancellationToken.None"/>.
    /// </param>
    /// <returns>The collection specified.</returns>
#if !NETSTANDARD2_0
    public override Task<DataTable> GetSchemaAsync(string collectionName, string?[]? restrictions, CancellationToken cancellationToken = default)
#else
    public Task<DataTable> GetSchemaAsync(string collectionName, string?[]? restrictions, CancellationToken cancellationToken = default)
#endif
        => throw new NotImplementedException();

    /// <summary>
    /// DB provider factory.
    /// </summary>
    protected override DbProviderFactory DbProviderFactory => throw new NotImplementedException();
}
