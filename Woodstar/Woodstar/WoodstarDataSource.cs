using System;
using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Woodstar.Data;
using Woodstar.Tds;
using Woodstar.Tds.SqlServer;
using Woodstar.Tds.Tds33;
using Woodstar.SqlServer;

namespace Woodstar;

record WoodstarDataSourceOptions
{
    internal static TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(30);

    public required EndPoint EndPoint { get; init; }
    public required string Username { get; init; }
    public string? Password { get; init; }
    public string? Database { get; init; }
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan CancellationTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MinPoolSize { get; init; } = 1;
    public int MaxPoolSize { get; init; } = 10;
    public int PoolSize
    {
        init
        {
            MinPoolSize = value;
            MaxPoolSize = value;
        }
    }

    /// <summary>
    /// CommandTimeout affects the first IO read after writing out a command.
    /// Default is infinite, where behavior purely relies on read and write timeouts of the underlying protocol.
    /// </summary>
    public TimeSpan CommandTimeout { get; init; } = DefaultCommandTimeout;
    public int AutoPrepareMinimumUses { get; set; }

    internal SqlServerOptions ToSqlServerOptions() => new()
    {
        EndPoint = EndPoint,
        Username = Username,
        Database = Database,
        Password = Password
    };

    internal bool Validate()
    {
        // etc
        return true;
    }
}

interface ISqlServerDatabaseInfoProvider
{
    SqlServerDatabaseInfo Get(SqlServerOptions pgOptions, TimeSpan timeSpan);
    ValueTask<SqlServerDatabaseInfo> GetAsync(SqlServerOptions pgOptions, CancellationToken cancellationToken = default);
}

class DefaultDatabaseInfoProvider: ISqlServerDatabaseInfoProvider
{
    SqlServerDatabaseInfo Create() => new();
    public SqlServerDatabaseInfo Get(SqlServerOptions pgOptions, TimeSpan timeSpan) => Create();
    public ValueTask<SqlServerDatabaseInfo> GetAsync(SqlServerOptions pgOptions, CancellationToken cancellationToken = default) => new(Create());
}

// Multiplexing
public partial class WoodstarDataSource : ICommandExecutionProvider
{
    Channel<OperationSource> CreateChannel() =>
        Channel.CreateUnbounded<OperationSource>(new UnboundedChannelOptions()
        {
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });

    struct MultiplexingItem: ISqlCommand
    {
        readonly ISqlCommand.BeginExecutionDelegate _beginExecutionDelegate;
        ISqlCommand.Values _values;
        CommandExecution _commandExecution;

        public MultiplexingItem(ISqlCommand.BeginExecutionDelegate beginExecutionDelegate, in ISqlCommand.Values values)
        {
            _beginExecutionDelegate = beginExecutionDelegate;
            _values = values;
            _commandExecution = default;
        }

        public CommandExecution CommandExecution
        {
            get => _commandExecution;
            set
            {
                // Null out the values so any heap objects can be freed before the entire operation is done.
                _values = default;
                _commandExecution = value;
            }
        }

        public ISqlCommand.Values GetValues() => _values;
        public ISqlCommand.BeginExecutionDelegate BeginExecutionMethod => throw new NotSupportedException();
        public CommandExecution BeginExecution(in ISqlCommand.Values values) => _beginExecutionDelegate(values);
    }

    static async Task MultiplexingCommandWriter(ChannelReader<OperationSource> reader, WoodstarDataSource dataSource)
    {
        const int writeThreshold = 1000; // TODO arbitrary constant, it works well though...
        var failedToEnqueue = false;
        while (failedToEnqueue || await reader.WaitToReadAsync())
        {
            var bytesWritten = 0L;
            TdsProtocol? protocol = null;
            OperationSource source = null!;
            try
            {
                if (failedToEnqueue || reader.TryRead(out source!))
                {
                    // Bind slot, this might throw.
                    await dataSource.ConnectionSource.BindAsync(source, dataSource.ConnectionTimeout, source.CancellationToken).ConfigureAwait(false);
                    protocol = (TdsProtocol)source.Protocol!;

                    if (failedToEnqueue)
                    {
                        failedToEnqueue = false;
                        if (!reader.TryRead(out source!))
                            protocol = null;
                    }
                }

                while (protocol is not null && !failedToEnqueue)
                {
                    // TODO this should probably only trigger if it also wrote a substantial amount (as a crude proxy for query compute cost)
                    var fewPending = false; // pending <= 2;

                    // D
                    // Write command, might throw.
                    var writeTask = WriteCommand(dataSource.GetDbDependencies().CommandWriter, source, flushHint: fewPending);

                    // Flush (if necessary).
                    var didFlush = fewPending;
                    // TODO we may want to keep track of protocols that are flushing so even if it has the least pending we don't pick it.
                    if (!didFlush && (!writeTask.IsCompleted || (bytesWritten += writeTask.Result.BytesWritten) >= writeThreshold || !reader.TryRead(out source!)))
                    {
                        // We don't need to await writeTask because flushasync will wait on the lock to release, which the writetask would be holding until completion.
                        // All FlushAsync code is inside an async method, any exceptions will be stored on the task.
                        var task = protocol.FlushAsync();
                        if (!task.IsCompletedSuccessfully)
                        {
                            var _ = task.AsTask().ContinueWith(t =>
                            {
                                try
                                {
                                    t.GetAwaiter().GetResult();
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine(ex.Message);
                                }
                            }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.DenyChildAttach);
                        }

                        protocol = null;
                    }
                    else if (didFlush)
                        protocol = null;
                    // Next.
                    else if (!protocol.TryStartOperation(source, cancellationToken: source.CancellationToken))
                        failedToEnqueue = true;
                }
            }
            catch (Exception openOrWriteException)
            {
                try
                {
                    // Connection is borked.
                    source?.TryComplete(openOrWriteException);
                }
                catch(Exception completionException)
                {
                    // TODO
                    Console.WriteLine(completionException.Message);
                }
            }
        }

        static ValueTask<WriteResult> WriteCommand(TdsCommandWriter commandWriter, OperationSource source, bool flushHint)
        {
            ref var command = ref TdsProtocol.GetDataRef<MultiplexingItem>(source);
            var commandContext = commandWriter.WriteAsync(source, command, flushHint, source.CancellationToken);
            command.CommandExecution = commandContext.GetCommandExecution();
            return commandContext.WriteTask;
        }
    }

    CommandExecution ICommandExecutionProvider.Get(in CommandContext context)
        => TdsProtocol.GetDataRef<MultiplexingItem>(context.ReadSlot).CommandExecution;

#if !NETSTANDARD2_0
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    internal async ValueTask<CommandContextBatch> WriteMultiplexingCommand<TCommand>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand: ISqlCommand
    {
        await EnsureInitializedAsync(cancellationToken);

        var item = new MultiplexingItem(command.BeginExecutionMethod, command.GetValues());
        var source = TdsProtocol.CreateUnboundOperationSource(item, cancellationToken);
        await ChannelWriter.WriteAsync(source, cancellationToken).ConfigureAwait(false);
        return CommandContext.Create(new IOCompletionPair(new (WriteResult.Unknown), source), this);
    }
}

public partial class WoodstarDataSource: DbDataSource, IConnectionFactory<TdsProtocol>
{
    readonly WoodstarDataSourceOptions _options;
    readonly SqlServerOptions _sqlServerOptions;
    readonly TdsProtocolOptions _tdsProtocolOptions;
    readonly ISqlServerDatabaseInfoProvider _databaseInfoProvider;
    readonly IFacetsTransformer _facetsTransformer;
    readonly SemaphoreSlim _lifecycleLock;

    // Initialized on the first real use.
    ConnectionSource<TdsProtocol>? _connectionSource;
    ChannelWriter<OperationSource>? _channelWriter;
    bool _isInitialized;
    DbDependencies? _dbDependencies;

    internal WoodstarDataSource(WoodstarDataSourceOptions options, TdsProtocolOptions tdsProtocolOptions, ISqlServerDatabaseInfoProvider? databaseInfoProvider = null)
    {
        options.Validate();
        _options = options;
        EndPointRepresentation = options.EndPoint.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 ? $"tcp://{options.EndPoint}" : options.EndPoint.ToString()!;
        _sqlServerOptions = options.ToSqlServerOptions();
        _tdsProtocolOptions = tdsProtocolOptions;
        _databaseInfoProvider = databaseInfoProvider ?? new DefaultDatabaseInfoProvider();
        _facetsTransformer = new IdentityFacetsTransformer();
        _lifecycleLock = new(1);
    }

    Exception NotInitializedException() => new InvalidOperationException("DataSource is not initialized yet, at least one connection needs to be opened first.");

    ChannelWriter<OperationSource> ChannelWriter => _channelWriter ?? throw NotInitializedException();
    ConnectionSource<TdsProtocol> ConnectionSource => _connectionSource ?? throw NotInitializedException();

    // Store the result if multiple dependencies are required. The instance may be switched out during reloading.
    // To prevent any inconsistencies without having to obtain a lock on the data we instead use an immutable instance.
    // All relevant depedencies are bundled to provide a consistent view, it's either all new or all old data.
    DbDependencies GetDbDependencies() => _dbDependencies ?? throw NotInitializedException();

    // False for datasources that dispatch commands across different backends.
    // Among other effects this impacts cacheability of state derived from unstable backend type information.
    // Its value should be static for the lifetime of the instance.
    internal bool IsPhysicalDataSource => true;
    // This is to get back to the multi-host datasource that owns its host sources.
    // It also helps commands to keep caches intact when switching sources from the same owner.
    internal WoodstarDataSource DataSourceOwner => this;

    internal TimeSpan ConnectionTimeout => _options.ConnectionTimeout;
    internal TimeSpan DefaultCancellationTimeout => _options.CancellationTimeout;
    internal TimeSpan DefaultCommandTimeout => _options.CommandTimeout;
    internal string Database => _options.Database ?? _options.Username;
    internal string EndPointRepresentation { get; }

    internal string ServerVersion => GetDbDependencies().DatabaseInfo.ServerVersion;

    int DbDepsRevision { get; set; }

     ValueTask Initialize(bool async, CancellationToken cancellationToken)
    {
        if (_isInitialized)
            return new ValueTask();

        return Core();

        async ValueTask Core()
        {
            if (async)
                await _lifecycleLock.WaitAsync(cancellationToken);
            else
                _lifecycleLock.Wait(cancellationToken);
            try
            {
                if (_isInitialized)
                    return;

                // We don't flow cancellationToken past this point, at least one thread has to finish the init.
                // We do DbDeps first as it may throw, otherwise we'd need to cleanup the other dependencies again.
                _dbDependencies = await CreateDbDeps(async, Timeout.InfiniteTimeSpan, CancellationToken.None); // TODO for now we could hook up the right things (init timeout?) later.

                var channel = CreateChannel();
                _channelWriter = channel.Writer;
                _connectionSource = new ConnectionSource<TdsProtocol>(this, _options.MaxPoolSize);
                var _ = Task.Run(() => MultiplexingCommandWriter(channel.Reader, this).ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted));
                _isInitialized = true;
                // We insert a memory barrier to make sure _isInitialized is published to all processors before we release the semaphore.
                // This is needed to be sure no other initialization will be started on another core that doesn't see _isInitialized = true yet but was already waiting for the lock.
                Thread.MemoryBarrier();
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        async ValueTask<DbDependencies> CreateDbDeps(bool async, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var databaseInfo = async
                ? _databaseInfoProvider.Get(_sqlServerOptions, timeout)
                : await _databaseInfoProvider.GetAsync(_sqlServerOptions, cancellationToken);

            return new DbDependencies(databaseInfo, DbDepsRevision++);
        }

    }

    void EnsureInitialized() => Initialize(false, CancellationToken.None).GetAwaiter().GetResult();
    ValueTask EnsureInitializedAsync(CancellationToken cancellationToken) => Initialize(true, cancellationToken);

    internal void PerformUserCancellation(Tds.Protocol protocol, TimeSpan timeout)
    {
        // TODO spin up a connection and write out cancel
    }

    internal CommandContextBatch WriteCommand<TCommand>(OperationSlot slot, TCommand command) where TCommand: ISqlCommand
    {
        EnsureInitialized();
        // TODO SingleThreadSynchronizationContext for sync writes happening async.
        return GetDbDependencies().CommandWriter.WriteAsync(slot, command, flushHint: true, CancellationToken.None);
    }

    internal async ValueTask<CommandContextBatch> WriteCommandAsync<TCommand>(OperationSlot slot, TCommand command, CancellationToken cancellationToken = default)
        where TCommand : ISqlCommand
    {
        await EnsureInitializedAsync(cancellationToken);
        return GetDbDependencies().CommandWriter.WriteAsync(slot, command, flushHint: true, cancellationToken);
    }

    internal async ValueTask<OperationSlot> GetSlotAsync(bool exclusiveUse, TimeSpan connectionTimeout, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await ConnectionSource.GetAsync(exclusiveUse, connectionTimeout, cancellationToken);
    }

    internal OperationSlot GetSlot(bool exclusiveUse, TimeSpan connectionTimeout)
    {
        EnsureInitialized();
        return ConnectionSource.Get(exclusiveUse, connectionTimeout);
    }

    TdsProtocol IConnectionFactory<TdsProtocol>.Create(TimeSpan timeout)
    {
        throw new NotImplementedException();
    }

    async ValueTask<TdsProtocol> IConnectionFactory<TdsProtocol>.CreateAsync(CancellationToken cancellationToken)
    {
        var socket = await SqlServerStreamConnection.ConnectAsync(_options.EndPoint, cancellationToken);
        return await TdsProtocol.StartAsync(socket.Writer, socket.Stream, _sqlServerOptions, _tdsProtocolOptions);
    }

    internal string SensitiveConnectionString => throw new NotImplementedException();
    public override string ConnectionString => ""; //TODO

    protected override DbConnection CreateDbConnection() => new WoodstarConnection(this);
    public new WoodstarConnection CreateConnection() => (WoodstarConnection)CreateDbConnection();
    public new WoodstarConnection OpenConnection() => (WoodstarConnection)base.OpenConnection();
    public new async ValueTask<WoodstarConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    protected override DbCommand CreateDbCommand(string? commandText = null)
        => new WoodstarCommand(commandText, this);

    public new WoodstarCommand CreateCommand(string? commandText = null)
        => (WoodstarCommand)CreateDbCommand(commandText);

    protected override void Dispose(bool disposing)
    {
    }

    internal AdoParameterContextFactory GetParameterContextFactory()
    {
        var dbDeps = GetDbDependencies();
        return new(_facetsTransformer, dbDeps.ParameterContextBuilderFactory);
    }

    // Internal for testing.
    internal class DbDependencies
    {
        public DbDependencies(SqlServerDatabaseInfo databaseInfo, int revision)
        {
            DatabaseInfo = databaseInfo;
            CommandWriter = new(databaseInfo, Encoding.Unicode);
            Revision = revision;
            ParameterContextBuilderFactory = GetParameterContextBuilder;
        }

        public SqlServerDatabaseInfo DatabaseInfo { get; }
        public TdsCommandWriter CommandWriter { get; }
        public int Revision { get; }

        public ParameterContextBuilderFactory ParameterContextBuilderFactory { get; }

        ParameterContextBuilder GetParameterContextBuilder(int length)
            => new(length, Revision);
    }
}
