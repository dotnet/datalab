using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Woodstar.Tds.Tds33;

enum CommandReaderState
{
    None = default,
    Initialized,
    Active,
    Completed,
    UnrecoverablyCompleted
}

/// <summary>
/// Specifies the type of SQL statement, e.g. SELECT
/// </summary>
enum StatementType
{
#pragma warning disable 1591
    Unknown = default,
    Select,
    Insert,
    Delete,
    Update,
    CreateTableAs,
    Move,
    Fetch,
    Copy,
    Other,
    Merge,
    Call
#pragma warning restore 1591
}

class TdsCommandReader
{
    readonly Action<TdsCommandReader>? _returnAction;
    CommandReaderState _state;

    // Recycled instances.
    readonly StartCommand _startCommand; // Quite big so it's a class.
    DataRowReader _rowReader; // Mutable struct, don't make readonly.
    CommandComplete _commandComplete; // Mutable struct, don't make readonly.
    // Set during InitializeAsync.
    Operation _operation;

    public TdsCommandReader(Encoding encoding, Action<TdsCommandReader>? returnAction = null)
    {
        _returnAction = returnAction;
        _startCommand = new();
        _commandComplete = new();
    }

    public CommandReaderState State => _state;
    public int FieldCount => throw new NotImplementedException();
    public bool HasRows => throw new NotImplementedException();

    public StatementType StatementType => throw new NotImplementedException();

    public ulong? RowsRetrieved
    {
        get
        {
            ThrowIfNotCompleted();
            if (RowsAffected.HasValue)
                return null;

            throw new NotImplementedException();
        }
    }
    public ulong? RowsAffected
    {
        get
        {
            ThrowIfNotCompleted();
            switch (StatementType)
            {
                case StatementType.Update:
                case StatementType.Insert:
                case StatementType.Delete:
                case StatementType.Copy:
                case StatementType.Move:
                case StatementType.Merge:
                    throw new NotImplementedException();
                default:
                    return null;
            }
        }
    }

#if !NETSTANDARD2_0
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
#endif
    // If this throws due to a protocol issue it will complete the operation and transition itself to CompleteUnrecoverably.
    // The operation itself will be completed with an exception if the known protocol state is indeterminate
    // or it will be completed 'succesfully' if the next operation could still succeed due to being able to reach a safe point.
    public async ValueTask InitializeAsync(CommandContext commandContext, CancellationToken cancellationToken = default)
    {
        if (_state is not CommandReaderState.None)
            ThrowNotReset();

        var opTask = commandContext.GetOperation();
        if (!opTask.IsCompleted)
            ThrowOpTaskNotReady();

        if (opTask.Result.IsCompleted)
            ThrowOpCompleted();

        var operation = opTask.Result;
        var startCommand = _startCommand;
        startCommand.Initialize(commandContext);
        // We cannot read ExecutionFlags before ReadMessageAsync (see StartCommand.Read for more details), so we can't split the execution paths at this point.
        try
        {
            startCommand = await ReadMessageAsync(startCommand, cancellationToken, operation).ConfigureAwait(false);
        }
        catch(Exception ex)
        {
            CompleteUnrecoverably(operation, ex, abortProtocol: !startCommand.IsReadyForNext);
            throw;
        }

        if (startCommand.TryGetCompleteResult(out var result))
        {
            _state = CommandReaderState.Initialized;
            Complete(result.CommandComplete);
            return;
        }

        _operation = operation;
        _rowReader = new();
        _state = CommandReaderState.Initialized;

        static void ThrowSqlException() => throw new NotImplementedException();
        static void ThrowOpTaskNotReady() => throw new ArgumentException("Operation task on given command context is not ready yet.", nameof(commandContext));
        static void ThrowOpCompleted() => throw new ArgumentException("Operation on given command context is already completed.", nameof(commandContext));
        static void ThrowNotReset() => throw new InvalidOperationException("CommandReader was not reset.");

        static void CloseStatement(CommandContext commandContext, StartCommand commandStart)
        {
            throw new NotImplementedException();
        }

        static void CompletePreparation(StartCommand commandStart)
        {
            throw new NotImplementedException();
        }
    }

    // TODO
    // public void Close()
    // {
    //     while (Read())
    //     {}
    //     CloseCore();
    // }

    public async ValueTask CloseAsync()
    {
        while (await ReadAsync().ConfigureAwait(false))
        {}
    }

    ValueTask<T> ReadMessageAsync<T>(T message, CancellationToken cancellationToken, Operation? operation = null) where T : IBackendMessage<TdsPacketHeader>
    {
        var op = operation ?? _operation;
        return Core(this, (Tds33Protocol)op.Protocol, op, message, cancellationToken);

#if !NETSTANDARD2_0
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
        static async ValueTask<T> Core(TdsCommandReader instance, Tds33Protocol protocol, Operation operation, T message, CancellationToken cancellationToken)
        {
            try
            {
                return await protocol.ReadMessageAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not TimeoutException && (ex is not OperationCanceledException || ex is OperationCanceledException oce && oce.CancellationToken != cancellationToken))
            {
                instance.CompleteUnrecoverably(operation, ex);
                throw;
            }
        }
    }

    TdsCommandReader ThrowIfNotInitialized()
    {
        if (_state is CommandReaderState.None)
            ThrowNotInitialized();

        return this;

        void ThrowNotInitialized() => throw new InvalidOperationException("Command reader wasn't initialized properly, this is a bug.");
    }

    TdsCommandReader ThrowIfNotCompleted()
    {
        if (_state is not CommandReaderState.Completed)
            ThrowNotCompleted();

        return this;

        void ThrowNotCompleted() => throw new InvalidOperationException("Command reader is not successfully completed.");
    }

    ValueTask HandleWritableParameters(bool async, IReadOnlyCollection<IParameterSession> writableParameters)
    {
        // TODO actually handle writable intput/output and output parameters.
        _startCommand.Session?.CloseWritableParameters();
        throw new NotImplementedException();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Task<bool> ReadAsync(CancellationToken cancellationToken = default)
    {
        // If we have writable parameters we immediately go async so we can fully read and process the first row.
        if (_state is CommandReaderState.Initialized && _startCommand.Session is { WritableParameters: { } writableParameters })
            return Core(this, default, writableParameters, cancellationToken);

        switch (_state)
        {
            case CommandReaderState.Initialized:
                // First row is already loaded.
                _state = CommandReaderState.Active;
                return Task.FromResult(true);
            case CommandReaderState.Active:
                ReadStatus status;
                // TODO benchmark adding a try catch by default again (last time it impacted perf quite a bit).
                if (BackendMessage.DebugEnabled)
                {
                    try
                    {
                        if (_rowReader.ReadNext(out status))
                            return Task.FromResult(true);
                    }
                    catch (Exception ex)
                    {
                        CompleteUnrecoverably(_operation, ex);
                        throw;
                    }
                }
                else if (_rowReader.ReadNext(out status))
                    return Task.FromResult(true);

                return status switch
                {
                    // TODO implement ConsumeData
                    // Only go async once we have to
                    ReadStatus.NeedMoreData or ReadStatus.AsyncResponse => Core(this, status, null, cancellationToken),
                    ReadStatus.Done or ReadStatus.InvalidData => CompleteCommand(this, unexpected: status is ReadStatus.InvalidData, cancellationToken).AsTask(),
                    _ => ThrowArgumentOutOfRange()
                };
            default:
                return HandleUncommon(this);
        }

        static async Task<bool> Core(TdsCommandReader instance, ReadStatus status, IReadOnlyCollection<IParameterSession>? writableParameters = null, CancellationToken cancellationToken = default)
        {
            // Skip the read if we haven't gotten any writable parameters, in that case we handle the given status (which is the most common reason for calling this method).
            var skipRead = writableParameters is null;
            // Only expect writableParameters if we're on the first row (CommandReaderState.Initialized).
            Debug.Assert(writableParameters is null || instance._state is CommandReaderState.Initialized);
            switch (instance._state)
            {
                case CommandReaderState.Initialized:
                    // First row is already loaded.
                    instance._state = CommandReaderState.Active;
                    if (writableParameters is not null)
                        await instance.HandleWritableParameters(async: true, writableParameters);
                    return true;
                case CommandReaderState.Active:
                    while (true)
                    {
                        if (BackendMessage.DebugEnabled)
                        {
                            try
                            {
                                if (!skipRead && instance._rowReader.ReadNext(out status))
                                    return true;
                            }
                            catch (Exception ex)
                            {
                                instance.CompleteUnrecoverably(instance._operation, ex);
                                throw;
                            }
                        }
                        else if (!skipRead && instance._rowReader.ReadNext(out status))
                            return true;
                        skipRead = false;

                        switch (status)
                        {
                            // TODO implement ConsumeData
                            case ReadStatus.NeedMoreData:
                                await BufferData(instance, cancellationToken).ConfigureAwait(false);
                                break;
                            case ReadStatus.Done or ReadStatus.InvalidData:
                                return await CompleteCommand(instance, unexpected: status is ReadStatus.InvalidData, cancellationToken).ConfigureAwait(false);
                            case ReadStatus.AsyncResponse:
                                await HandleAsyncResponse(instance, cancellationToken).ConfigureAwait(false);
                                break;
                            default:
                                return await ThrowArgumentOutOfRange().ConfigureAwait(false);
                        }
                    }
                default:
                    return await HandleUncommon(instance).ConfigureAwait(false);
            }
        }

        static Task<bool> HandleUncommon(TdsCommandReader instance)
        {
            switch (instance._state)
            {
                case CommandReaderState.Completed:
                case CommandReaderState.UnrecoverablyCompleted:
                    return Task.FromResult(false);
                case CommandReaderState.None:
                    instance.ThrowIfNotInitialized();
                    return Task.FromResult(false);
                default:
                {
                    var ex = new ArgumentOutOfRangeException();
                    instance.CompleteUnrecoverably(instance._operation, ex);
                    return Task.FromException<bool>(ex);
                }
            }
        }

#if !NETSTANDARD2_0
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
#endif
        static async ValueTask BufferData(TdsCommandReader instance, CancellationToken cancellationToken = default)
        {
            var result = await instance.ReadMessageAsync(
                new ExpandBuffer(instance._rowReader.ResumptionData, instance._rowReader.Consumed),
                cancellationToken).ConfigureAwait(false);
            instance._rowReader.ExpandBuffer(result.Buffer);
        }

#if !NETSTANDARD2_0
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
        static async ValueTask<bool> CompleteCommand(TdsCommandReader instance, bool unexpected, CancellationToken cancellationToken = default)
        {
            if (unexpected)
            {
                // We don't need to pass an exception, InvalidData kills the connection.
                instance.CompleteUnrecoverably(instance._operation);
                return false;
            }

            var result = await instance.ReadMessageAsync(
                new CompleteCommand(instance._rowReader.ResumptionData, instance._rowReader.Consumed),
                cancellationToken).ConfigureAwait(false);
            instance.Complete(result.CommandComplete);
            return false;
        }

        static ValueTask HandleAsyncResponse(TdsCommandReader instance, CancellationToken cancellationToken = default)
        {
            // TODO implement async response, even though technically the protocol as implemented in postgres never mixes async responses and rows (see pg docs).
            throw new NotImplementedException();
        }

        static Task<bool> ThrowArgumentOutOfRange() => Task.FromException<bool>(new ArgumentOutOfRangeException());
    }

    void CompleteUnrecoverably(Operation operation, Exception? ex = null, bool abortProtocol = true)
    {
        if (_startCommand.Session is { } session)
            session.CloseWritableParameters();

        _state = CommandReaderState.UnrecoverablyCompleted;
        // We don't have to abort if we were able to advance the protocol to a safe point (RFQ).
        operation.Complete(abortProtocol ? ex : null);
    }

    void Complete(CommandComplete commandComplete)
    {
        _state = CommandReaderState.Completed;
        _commandComplete = commandComplete;
        _operation.Complete();
    }

    public void Reset()
    {
        _state = CommandReaderState.None;
        _startCommand.Reset();
        _rowReader = default;
        _returnAction?.Invoke(this);
    }

    class StartCommand : ITds33BackendMessage
    {
        public ICommandSession Session { get; }
        public bool IsReadyForNext { get; set; }

        public void Reset()
        {

        }

        public void Initialize(CommandContext commandContext)
        {
            throw new NotImplementedException();
        }

        public ReadStatus Read(ref MessageReader<TdsPacketHeader> reader)
        {
            throw new NotImplementedException();
        }

        public bool TryGetCompleteResult(out CompleteCommand o)
        {
            throw new NotImplementedException();
        }
    }

    struct CommandComplete
    {

    }

    struct CompleteCommand : ITds33BackendMessage
    {
        public CompleteCommand(MessageReader<TdsPacketHeader>.ResumptionData rowReaderResumptionData, long rowReaderConsumed)
        {
            throw new NotImplementedException();
        }

        public CommandComplete CommandComplete => new();
        public ReadStatus Read(ref MessageReader<TdsPacketHeader> reader)
        {
            throw new NotImplementedException();
        }
    }
    
    struct ExpandBuffer : ITds33BackendMessage
    {
        readonly MessageReader<TdsPacketHeader>.ResumptionData _resumptionData;
        readonly long _consumed;
        bool _resumed;

        public ExpandBuffer(MessageReader<TdsPacketHeader>.ResumptionData resumptionData, long consumed)
        {
            _resumptionData = resumptionData;
            _consumed = consumed;
        }

        public ReadOnlySequence<byte> Buffer { get; private set; }

        public ReadStatus Read(ref MessageReader<TdsPacketHeader> reader)
        {
            if (_resumed)
            {
                // When we get resumed all we do is store the new buffer.
                Buffer = reader.Sequence;
                // Return a reader that has not consumed anything, this ensures no part of the buffer will be advanced out from under us.
                reader = MessageReader<TdsPacketHeader>.Create(reader.Sequence);
                return ReadStatus.Done;
            }

            // Create a reader that has the right consumed state and header data for the outer read loop to figure out the next size.
            if (_resumptionData.IsDefault)
            {
                // We don't have enough data to read the next header, just advance to consumed.
                // The outer read loop will read at minimum a header length worth of new data before resuming.
                reader = MessageReader<TdsPacketHeader>.Create(reader.Sequence);
                reader.Advance(_consumed);
            }
            else
                reader = _consumed == 0 ? MessageReader<TdsPacketHeader>.Resume(reader.Sequence, _resumptionData) : MessageReader<TdsPacketHeader>.Recreate(reader.Sequence, _resumptionData, _consumed);
            _resumed = true;
            return ReadStatus.NeedMoreData;
        }
    }

    struct DataRowReader
    {
        public MessageReader<TdsPacketHeader>.ResumptionData ResumptionData { get; set; }
        public long Consumed { get; set; }

        public void ExpandBuffer(ReadOnlySequence<byte> resultBuffer)
        {
            throw new NotImplementedException();
        }

        public bool ReadNext(out ReadStatus status)
        {
            throw new NotImplementedException();
        }
    }
}
