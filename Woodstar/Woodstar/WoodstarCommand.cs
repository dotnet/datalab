using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Woodstar.Tds;

namespace Woodstar;

static class CommandBehaviorExtensions
{
    public static ExecutionFlags ToExecutionFlags(this CommandBehavior commandBehavior)
    {
        // Remove any irrelevant flags and mask the rest of the range for ExecutionFlags so users can't leak any other flags through.
        const int allFlags = (int)CommandBehavior.CloseConnection * 2 - 1; // 2^6 - 1.
        return (ExecutionFlags)(commandBehavior & ~((CommandBehavior)int.MaxValue - allFlags | CommandBehavior.CloseConnection | CommandBehavior.SingleResult));
    }
}

readonly struct CommandCache
{
    public ParameterCache ParameterCache { get; init; }
    public StatementCache StatementCache { get; init; }
}

readonly struct StatementCache
{
    public SizedString SizedString { get; init; }
}

// Implementation
public sealed partial class WoodstarCommand
{
    object? _dataSourceOrConnection;
    CommandType _commandType = CommandType.Text;
    TimeSpan _commandTimeout = WoodstarDataSourceOptions.DefaultCommandTimeout;
    readonly WoodstarTransaction? _transaction;
    string _userCommandText;
    bool _disposed;
    WoodstarParameterCollection? _parameterCollection;
    bool _preparationRequested;
    CommandCache _cache;
    object SyncObj => this; // DbCommand base also locks on 'this'.

    WoodstarCommand(string? commandText, WoodstarConnection? conn, WoodstarTransaction? transaction, WoodstarDataSource? dataSource = null)
    {
        GC.SuppressFinalize(this);
        _userCommandText = commandText ?? string.Empty;
        _transaction = transaction;
        if (conn is not null)
        {
            _dataSourceOrConnection = conn;
            _commandTimeout = conn.DefaultCommandTimeout;
        }
        else if (dataSource is not null)
        {
            _dataSourceOrConnection = dataSource;
            _commandTimeout = dataSource.DefaultCommandTimeout;
        }
    }

    void SetCommandText(string? value)
    {
        if (!ReferenceEquals(value, _userCommandText))
        {
            _preparationRequested = false;
            ResetCache();
            _userCommandText = value ?? string.Empty;
        }
    }

    CommandCache ReadCache()
    {
        lock (SyncObj)
            return _cache;
    }

    /// SetCache will not dispose any individual fields as they may be aliased/reused in the new value.
    void SetCache(in CommandCache value)
    {
        lock (SyncObj)
            _cache = value;
    }

    /// ResetCache will dispose any individual fields.
    void ResetCache()
    {
        lock (SyncObj)
        {
            if (!_cache.ParameterCache.IsDefault)
                _cache.ParameterCache.Dispose();
            _cache = default;
        }
    }

    // Captures any per call state and merges it with the remaining, less volatile, WoodstarCommand state during GetValues.
    // This allows WoodstarCommand to be concurrency safe (an execution is entirely isolated but command mutations are not thread safe), store an instance on a static and go!
    // TODO we may want to lock values when _preparationRequested.
    readonly struct Command: ISqlCommand
    {
        static readonly ISqlCommand.BeginExecutionDelegate BeginExecutionDelegate = BeginExecutionCore;

        readonly WoodstarCommand _instance;
        readonly WoodstarParameterCollection? _parameters;
        readonly ExecutionFlags _additionalFlags;

        public Command(WoodstarCommand instance, WoodstarParameterCollection? parameters, ExecutionFlags additionalFlags)
        {
            _instance = instance;
            _parameters = parameters;
            _additionalFlags = additionalFlags;
        }

        (Statement?, StatementCache?, ExecutionFlags) GetStatement(StatementCache cache, WoodstarDataSource dataSource, string statementText)
        {
            var statement = (Statement?)null;

            var flags = statement switch
            {
                { IsComplete: true } => ExecutionFlags.Prepared,
                { } => ExecutionFlags.Preparing,
                _ => ExecutionFlags.Unprepared
            };

            return (statement, default, flags);
        }

        // TODO rewrite if necessary (should have happened already, to allow for batching).
        string GetStatementText()
        {
            return _instance._userCommandText;
        }

        // statementText is expected to be null when we have a prepared statement.
        (ParameterContext, ParameterCache?) BuildParameterContext(WoodstarDataSource dataSource, string? statementText, WoodstarParameterCollection? parameters, ParameterCache cache)
        {
            if (parameters is null || parameters.Count == 0)
                // We return null (no change) for the cache here as we rely on command text changes to clear any caches.
                return (ParameterContext.Empty, null);

            return dataSource.GetParameterContextFactory().Create(parameters, cache, createCache: true);
        }

        public ISqlCommand.Values GetValues()
        {
            var cache = _instance.ReadCache();
            var dataSource = _instance.TryGetDataSource(out var s) ? s : _instance.GetConnection().DbDataSource;
            var statementText = GetStatementText();
            var (parameterContext, parameterCache) = BuildParameterContext(dataSource, statementText, _parameters, cache.ParameterCache);
            var (statement, statementCache, executionFlags) = GetStatement(cache.StatementCache, dataSource, statementText);

            if (parameterCache is not null || statementCache is not null)
            {
                // If we got an update we should cleanup the current cache.
                if (parameterCache is not null && !cache.ParameterCache.IsDefault)
                    cache.ParameterCache.Dispose();
                _instance.SetCache(new CommandCache { ParameterCache = parameterCache ?? cache.ParameterCache, StatementCache = statementCache ?? cache.StatementCache });
            }

            return new()
            {
                StatementText = (SizedString)statementText,
                ExecutionFlags = executionFlags | _additionalFlags,
                Statement = statement,
                ExecutionTimeout = _instance._commandTimeout,
                ParameterContext = parameterContext,
                State = dataSource,
            };
        }

        public ISqlCommand.BeginExecutionDelegate BeginExecutionMethod => BeginExecutionDelegate;
        public CommandExecution BeginExecution(in ISqlCommand.Values values) => BeginExecutionCore(values);

        // This is a static function to assure CreateExecution only has dependencies on clearly passed in state.
        // Any unexpected _instance dependencies would undoubtedly cause fun races.
        static CommandExecution BeginExecutionCore(in ISqlCommand.Values values)
        {
            var executionFlags = values.ExecutionFlags;
            var statement = values.Statement;
            Debug.Assert(values.State is WoodstarDataSource);
            Debug.Assert(executionFlags.HasUnprepared() || statement is not null);
            // We only allocate to facilitate preparation or output params, both are fairly uncommon operations.
            AdoCommandSession? session = null;
            if (executionFlags.HasPreparing() || values.ParameterContext.HasWritableParamSessions())
                session = new AdoCommandSession((WoodstarDataSource)values.State, values);

            var commandExecution = executionFlags switch
            {
                _ when executionFlags.HasPrepared() => CommandExecution.Create(executionFlags, values.CommandFlags, statement!),
                _ when session is not null => CommandExecution.Create(executionFlags, values.CommandFlags, session),
                _ => CommandExecution.Create(executionFlags, values.CommandFlags)
            };

            return commandExecution;
        }
    }

    void ThrowIfDisposed()
    {
        if (_disposed)
            ThrowObjectDisposed();

        static void ThrowObjectDisposed() => throw new ObjectDisposedException(nameof(WoodstarCommand));
    }

    bool TryGetConnection([NotNullWhen(true)]out WoodstarConnection? connection)
    {
        connection = _dataSourceOrConnection as WoodstarConnection;
        return connection is not null;
    }
    WoodstarConnection GetConnection() => TryGetConnection(out var connection) ? connection : throw new NullReferenceException("Connection is null.");
    WoodstarConnection.CommandWriter GetCommandWriter() => GetConnection().GetCommandWriter();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool TryGetDataSource([NotNullWhen(true)]out WoodstarDataSource? connection)
    {
        connection = _dataSourceOrConnection as WoodstarDataSource;
        return connection is not null;
    }

    // Only for DbConnection commands, throws for DbDataSource commands (alternatively we can choose to ignore it).
    bool HasCloseConnection(CommandBehavior behavior) => (behavior & CommandBehavior.CloseConnection) == CommandBehavior.CloseConnection;
    void ThrowIfHasCloseConnection(CommandBehavior behavior)
    {
        if (HasCloseConnection(behavior))
            ThrowHasCloseConnection();

        void ThrowHasCloseConnection() => throw new ArgumentException($"Cannot pass {nameof(CommandBehavior.CloseConnection)} to a DbDataSource command, this is only valid when a command has a connection.");
    }

    WoodstarDataReader ExecuteDataReader(CommandBehavior behavior)
    {
        ThrowIfDisposed();
        if (TryGetDataSource(out var dataSource))
        {
            ThrowIfHasCloseConnection(behavior);
            // Pick a connection and do the write ourselves, connectionless command execution for sync paths :)
            var slot = dataSource.GetSlot(exclusiveUse: false, dataSource.ConnectionTimeout);
            var command = dataSource.WriteCommand(slot, CreateCommand(null, behavior));
            return WoodstarDataReader.Create(async: false, new ValueTask<CommandContextBatch>(command)).GetAwaiter().GetResult();
        }
        else
        {
            var command = GetCommandWriter().WriteCommandAsync(allowPipelining: false, CreateCommand(null, behavior), HasCloseConnection(behavior));
            return WoodstarDataReader.Create(async: false, command).GetAwaiter().GetResult();
        }
    }

    ValueTask<WoodstarDataReader> ExecuteDataReaderAsync(WoodstarParameterCollection? parameters, CommandBehavior behavior, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (TryGetDataSource(out var dataSource))
        {
            ThrowIfHasCloseConnection(behavior);
            // Pick a connection and do the write ourselves, connectionless command execution :)
            var slot = dataSource.GetSlot(exclusiveUse: false, dataSource.ConnectionTimeout);
            var command = dataSource.WriteCommandAsync(slot, CreateCommand(parameters, behavior), cancellationToken);
            return WoodstarDataReader.Create(async: true, command);
        }
        else
        {
            var command = GetCommandWriter().WriteCommandAsync(allowPipelining: true, CreateCommand(parameters, behavior), HasCloseConnection(behavior), cancellationToken);
            return WoodstarDataReader.Create(async: true, command);
        }
    }

    Command CreateCommand(WoodstarParameterCollection? parameters, CommandBehavior behavior)
        => new(this, parameters ?? _parameterCollection, behavior.ToExecutionFlags());

    async ValueTask DisposeCore(bool async)
    {
        if (_disposed)
            return;
        _disposed = true;

        // TODO, unprepare etc.
        await new ValueTask().ConfigureAwait(false);

        ResetCache();
        base.Dispose(true);
    }
}

// Public surface & ADO.NET
public sealed partial class WoodstarCommand: DbCommand
{
    public WoodstarCommand() : this(null, null, null) {}
    public WoodstarCommand(string? commandText) : this(commandText, null, null) {}
    public WoodstarCommand(string? commandText, WoodstarConnection? conn) : this(commandText, conn, null) {}
    public WoodstarCommand(string? commandText, WoodstarConnection? conn, WoodstarTransaction? transaction)
        : this(commandText, conn, transaction, null) {} // Points to the private constructor.
    internal WoodstarCommand(string? commandText, WoodstarDataSource dataSource)
        : this(commandText, null, null, dataSource: dataSource) {} // Points to the private constructor.

    public override void Prepare()
    {
        ThrowIfDisposed();
        _preparationRequested = true;
    }

    [AllowNull]
    public override string CommandText
    {
        get => _userCommandText;
        set => SetCommandText(value);
    }

    public override int CommandTimeout
    {
        get => (int)_commandTimeout.TotalSeconds;
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Cannot be zero or negative.");
            _commandTimeout = TimeSpan.FromSeconds(value);
        }
    }

    public override CommandType CommandType
    {
        get => _commandType;
        set
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException();
            _commandType = value;
        }
    }

    /// <summary>
    /// Setting this property is ignored by Woodstar as its values are not respected.
    /// Gets or sets how command results are applied to the DataRow when used by the
    /// DbDataAdapter.Update(DataSet) method.
    /// </summary>
    /// <value>One of the <see cref="System.Data.UpdateRowSource"/> values.</value>
    public override UpdateRowSource UpdatedRowSource
    {
        get => UpdateRowSource.None;
        set { }
    }

    public new WoodstarParameterCollection Parameters => _parameterCollection ??= new();

    /// <summary>
    /// Setting this property is ignored by Woodstar. PostgreSQL only supports a single transaction at a given time on
    /// a given connection, and all commands implicitly run inside the current transaction started via
    /// <see cref="WoodstarConnection.BeginTransaction()"/>
    /// </summary>
    public new WoodstarTransaction? Transaction => _transaction;

    public override bool DesignTimeVisible { get; set; }

    public override void Cancel()
    {
        // We can't throw in connectionless scenarios as dapper etc expect this method to work.
        // TODO We might be able to support it on connectionless commands by creating protocol level support for it, not today :)
        if (!TryGetConnection(out var connection) || !connection.ConnectionOpInProgress)
            return;

        connection.PerformUserCancellation();
    }

    public override int ExecuteNonQuery()
    {
        throw new NotImplementedException();
    }

    public override object? ExecuteScalar()
    {
        throw new NotImplementedException();
    }

    public new WoodstarDataReader ExecuteReader()
        => ExecuteDataReader(CommandBehavior.Default);
    public new WoodstarDataReader ExecuteReader(CommandBehavior behavior)
        => ExecuteDataReader(behavior);

    public new Task<WoodstarDataReader> ExecuteReaderAsync(CancellationToken cancellationToken = default)
        => ExecuteDataReaderAsync(null, CommandBehavior.Default, cancellationToken).AsTask();
    public new Task<WoodstarDataReader> ExecuteReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken = default)
        => ExecuteDataReaderAsync(null, behavior, cancellationToken).AsTask();

    public ValueTask<WoodstarDataReader> ExecuteReaderAsync(WoodstarParameterCollection? parameters, CancellationToken cancellationToken = default)
        => ExecuteDataReaderAsync(parameters, CommandBehavior.Default, cancellationToken);

    public ValueTask<WoodstarDataReader> ExecuteReaderAsync(WoodstarParameterCollection? parameters, CommandBehavior behavior, CancellationToken cancellationToken = default)
        => ExecuteDataReaderAsync(parameters, behavior, cancellationToken);

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        => ExecuteDataReader(behavior);
    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        => await ExecuteDataReaderAsync(null, behavior, cancellationToken);

    protected override DbParameter CreateDbParameter() => WoodstarDbParameter.Create();
    protected override DbConnection? DbConnection
    {
        get => _dataSourceOrConnection as WoodstarConnection;
        set
        {
            ThrowIfDisposed();
            if (value is not WoodstarConnection conn)
                throw new ArgumentException($"Value is not an instance of {nameof(WoodstarConnection)}.", nameof(value));

            if (TryGetConnection(out var current))
            {
                if (!ReferenceEquals(current.DbDataSource.DataSourceOwner, conn.DbDataSource.DataSourceOwner))
                    ResetCache();
            }
            else
                throw new InvalidOperationException("This is a DbDataSource command and cannot be assigned to connections.");

            _dataSourceOrConnection = conn;
        }
    }

    protected override DbParameterCollection DbParameterCollection => Parameters;
    protected override DbTransaction? DbTransaction { get => Transaction; set {} }

#if !NETSTANDARD2_0
    public override ValueTask DisposeAsync()
#else
    public ValueTask DisposeAsync()
#endif
        => DisposeCore(async: true);

    protected override void Dispose(bool disposing)
        => DisposeCore(false).GetAwaiter().GetResult();
}
