using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Woodstar.Buffers;
using Woodstar.Pipelines;
using Woodstar.Tds.Messages;
using Woodstar.Tds.Packets;
using Woodstar.Tds.SqlServer;
using Woodstar.Tds.Tokens;

namespace Woodstar.Tds.Tds33;

record TdsProtocolOptions
{
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaximumMessageChunkSize { get; init; } = 8192 / 2;
    public int FlushThreshold { get; init; } = 8192 / 2;
}

// TODO we need to throw/tear down on client_encoding change notices, it's is not a permitted change to do.
class TdsProtocol : Protocol
{
    static TdsProtocolOptions DefaultProtocolOptions { get; } = new();
    readonly TdsProtocolOptions _protocolOptions;
    readonly BufferingStreamReader _reader;
    readonly PipeWriter _pipeWriter;

    readonly ResettableFlushControl _flushControl;

    // Lock held for the duration of an individual message write or an entire exclusive use.
    readonly SemaphoreSlim _messageWriteLock = new(1);
    readonly Queue<TdsOperationSource> _operations;

    readonly TdsOperationSource _operationSourceSingleton;
    readonly TdsOperationSource _exclusiveOperationSourceSingleton;

    readonly ResettableStreamingWriter<TdsPacketWriter> _streamingWriter;
    readonly TdsPacketWriter _tdsPacketWriter;
    readonly TokenReader _tokenReader;

    // The pool exists as there is a timeframe between a reader completing (and its slot) and the owner resetting the reader.
    // During this time the next operation in the queue might request a reader as well.
    readonly ObjectPool<CommandReader> _commandReaderPool;
    // Arbitrary but it should not be the case that it takes more than 5 ops all completing before a single reader is returned again.
    const int maxReaderPoolSize = 5;

    volatile ProtocolState _state = ProtocolState.Created;
    volatile Exception? _completingException;
    volatile int _pendingExclusiveUses;

    TdsProtocol(PipeWriter writer, Stream stream, Encoding encoding, TdsProtocolOptions? protocolOptions = null)
    {
        _protocolOptions = protocolOptions ?? DefaultProtocolOptions;
        _pipeWriter = writer;
        _flushControl = new ResettableFlushControl(writer, _protocolOptions.WriteTimeout, Math.Max(1500 , _protocolOptions.FlushThreshold));
        var pipeStreamingWriter = new PipeStreamingWriter(_pipeWriter);
        _tdsPacketWriter = new TdsPacketWriter(pipeStreamingWriter, 4096);
        _streamingWriter = new ResettableStreamingWriter<TdsPacketWriter>(_tdsPacketWriter);
        _reader = new BufferingStreamReader(new TdsPacketStream(stream));
        _tokenReader = new TokenReader(_reader);
        _operations = new Queue<TdsOperationSource>();
        _operationSourceSingleton = new TdsOperationSource(this, exclusiveUse: false, pooled: true);
        _exclusiveOperationSourceSingleton = new TdsOperationSource(this, exclusiveUse: true, pooled: true);
        _commandReaderPool = new(pool =>
        {
            var returnAction = pool.Return;
            return () => throw new NotImplementedException();
        }, maxReaderPoolSize);
        Encoding = encoding;
    }

    object InstanceToken => this;
    object SyncObj => _operations;

    bool IsCompleted => _state is ProtocolState.Completed;

    public TokenReader Reader => _tokenReader;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    TdsOperationSource ThrowIfInvalidSlot(OperationSlot slot, bool allowUnbound = false, bool allowActivated = true)
    {
        if (slot is not TdsOperationSource source ||
            (!allowUnbound && (source.IsUnbound || !ReferenceEquals(source.InstanceToken, InstanceToken))) ||
            source.IsCompleted || (!allowActivated && source.IsActivated))
        {
            HandleUncommon();
            return null!;
        }

        return source;

        void HandleUncommon()
        {
            if (slot is not TdsOperationSource source)
                throw new ArgumentException("Cannot accept this type of slot.", nameof(slot));

            switch (allowUnbound)
            {
                case false when source.IsUnbound:
                    throw new ArgumentException("Cannot accept an unbound slot.", nameof(slot));
                case false when !ReferenceEquals(source.InstanceToken, InstanceToken):
                    throw new ArgumentException("Cannot accept a slot for some other connection.", nameof(slot));
            }

            if (source.IsCompleted || (!allowActivated && source.IsActivated))
                throw new ArgumentException("Cannot accept a completed operation.", nameof(slot));
        }
    }

    static bool IsTimeoutOrCallerOCE(Exception ex, CancellationToken cancellationToken)
        => ex is TimeoutException || (ex is OperationCanceledException oce && oce.CancellationToken == cancellationToken);

    public override ValueTask FlushAsync(CancellationToken cancellationToken = default) => FlushAsyncCore(null, cancellationToken);
    public override ValueTask FlushAsync(OperationSlot op, CancellationToken cancellationToken = default) => FlushAsyncCore(op, cancellationToken);
    async ValueTask FlushAsyncCore(OperationSlot? op = null, CancellationToken cancellationToken = default)
    {
        // TODO actually check if the passed slot is the head.
        if (op is null && !_messageWriteLock.Wait(0))
            await _messageWriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        // Other messages could have flushed in between us waiting for the lock.
        if (_pipeWriter.UnflushedBytes == 0)
            return;

        _flushControl.Initialize();
        try
        {
            await _flushControl.FlushAsync(observeFlushThreshold: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsTimeoutOrCallerOCE(ex, cancellationToken))
        {
            MoveToComplete(ex);
            throw;
        }
        finally
        {
            if (!IsCompleted)
            {
                // We must reset the writer as it holds onto an output segment that is now flushed.
                _flushControl.Reset();

                if (op is null)
                    _messageWriteLock.Release();
            }
        }
    }

    Encoding Encoding { get; }

    public override CommandReader GetCommandReader() => throw new NotImplementedException();

    public IOCompletionPair WriteMessageAsync<T>(OperationSlot slot, T message, bool flushHint = true, CancellationToken cancellationToken = default) where T : IFrontendMessage
        => WriteMessageBatchAsync(slot, (batchWriter, message, cancellationToken) => batchWriter.WriteMessageAsync(message, cancellationToken), message, flushHint, cancellationToken);

    public readonly struct BatchWriter
    {
        readonly TdsProtocol _instance;
        internal BatchWriter(TdsProtocol instance) => _instance = instance;

        public ValueTask WriteMessageAsync<T>(T message, CancellationToken cancellationToken = default) where T : IFrontendMessage
        {
            var dataStreamWriter = _instance._tdsPacketWriter;
            var streamingWriter = _instance._streamingWriter;
            if (message.CanWriteSynchronously)
            {
                try
                {
                    var messageType = T.MessageType;
                    dataStreamWriter.StartMessage(messageType.Type, messageType.Status);
                    var buffer = new BufferWriter<TdsPacketWriter>(dataStreamWriter);
                    message.Write(ref buffer);
                    Debug.Assert(buffer.BufferedBytes > 0, "Message writer should not flush all data as this may prevent packet end of message finalization.");
                    dataStreamWriter.Advance(buffer.BufferedBytes, endMessage: true);
                    return new ValueTask();
                }
                finally
                {
                    streamingWriter.Reset();
                }
            }

            return WriteAsync(dataStreamWriter, streamingWriter, message, cancellationToken);

            static async ValueTask WriteAsync(TdsPacketWriter dataStreamWriter, ResettableStreamingWriter<TdsPacketWriter> streamingWriter, T message, CancellationToken cancellationToken = default)
            {
                try
                {
                    var messageType = T.MessageType;
                    dataStreamWriter.StartMessage(messageType.Type, messageType.Status);
                    await message.WriteAsync(streamingWriter, cancellationToken);
                    Debug.Assert(streamingWriter.BufferedBytes > 0, "Message writer should not flush all data as this may prevent packet end of message finalization.");
                    dataStreamWriter.Advance(streamingWriter.BufferedBytes, endMessage: true);
                }
                finally
                {
                    streamingWriter.Reset();
                }
            }
        }
    }

    public IOCompletionPair WriteMessageBatchAsync<TState>(OperationSlot slot, Func<BatchWriter, TState, CancellationToken, ValueTask> batchWriter, TState state, bool flushHint = true, CancellationToken cancellationToken = default)
    {
        var source = ThrowIfInvalidSlot(slot);

        return new IOCompletionPair(Core(this, source, batchWriter, state, flushHint, cancellationToken), slot);

#if !NETSTANDARD2_0
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
        static async ValueTask<WriteResult> Core(TdsProtocol instance, TdsOperationSource source, Func<BatchWriter, TState, CancellationToken, ValueTask> batchWriter, TState state, bool flushHint = true, CancellationToken cancellationToken = default)
        {
            if (source.WriteSlot.Status != TaskStatus.RanToCompletion)
                await source.WriteSlot.ConfigureAwait(false);

            instance._flushControl.Initialize();
            try
            {
                await batchWriter.Invoke(new BatchWriter(instance), state, cancellationToken).ConfigureAwait(false);
                if (instance._flushControl.WriterCompleted)
                    instance.MoveToComplete();
                else if (flushHint && instance._flushControl.UnflushedBytes > 0)
                    await instance._flushControl.FlushAsync(observeFlushThreshold: false, cancellationToken).ConfigureAwait(false);

                // Debug.Assert(instance._defaultMessageWriter.BytesCommitted > 0);
                return new WriteResult(100);
            }
            catch (Exception ex) when (!IsTimeoutOrCallerOCE(ex, cancellationToken))
            {
                instance.MoveToComplete(ex);
                throw;
            }
            finally
            {
                if (!instance.IsCompleted)
                {
                    instance._flushControl.Reset();

                    // TODO we can always add a flag to control this for non exclusive use (e.g. something like WriteFlags.EndWrites)
                    if (!source.IsExclusiveUse)
                    {
                        var result = source.EndWrites(instance._messageWriteLock);
                        Debug.Assert(result, "Could not end write slot.");
                    }
                }
            }
        }
    }

    void MoveToComplete(Exception? exception = null, bool brokenRead = false)
    {
        lock (SyncObj)
        {
            if (_state is ProtocolState.Completed)
                return;
            _state = ProtocolState.Completed;
        }

        _completingException = exception;
        if (brokenRead)
        {
            _pipeWriter.Complete(exception);
        }
        else
        {
            _pipeWriter.Complete(exception);
        }
        _messageWriteLock.Dispose();
        _flushControl.Dispose();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ValueTask WriteMessageUnsynchronized<TWriter, T>(TdsPacketWriter tdsPacketWriter, StreamingWriter<TWriter> streamingWriter, T message, CancellationToken cancellationToken = default)
        where TWriter : IStreamingWriter<byte> where T : IFrontendMessage
    {
        if (message.CanWriteSynchronously)
        {
            var buffer = new BufferWriter<TdsPacketWriter>(tdsPacketWriter);
            message.Write(ref buffer);
            Debug.Assert(buffer.BufferedBytes > 0, "Message writer should not flush all data as this may prevent packet end of message finalization.");
            tdsPacketWriter.Advance(buffer.BufferedBytes, endMessage: true);
            return new ValueTask();
        }

        return WriteAsync(tdsPacketWriter, streamingWriter, message, cancellationToken);

        static async ValueTask WriteAsync(TdsPacketWriter dataStreamWriter, StreamingWriter<TWriter> streamingWriter, T message, CancellationToken cancellationToken = default)
        {
            await message.WriteAsync(streamingWriter, cancellationToken);
            Debug.Assert(streamingWriter.BufferedBytes > 0, "Message writer should not flush all data as this may prevent packet end of message finalization.");
            dataStreamWriter.Advance(streamingWriter.BufferedBytes, endMessage: true);
        }
    }
//
//     public ValueTask<T> ReadMessageAsync<T>(T message, CancellationToken cancellationToken = default) where T : IBackendMessage<TdsPacketHeader> => ProtocolReader.ReadAsync(this, message, cancellationToken);
//     public ValueTask<T> ReadMessageAsync<T>(CancellationToken cancellationToken = default) where T : struct, IBackendMessage<TdsPacketHeader> => ProtocolReader.ReadAsync(this, new T(), cancellationToken);
//     public T ReadMessage<T>(T message, TimeSpan timeout = default) where T : IBackendMessage<TdsPacketHeader> => ProtocolReader.Read(this, message, timeout);
//     public T ReadMessage<T>(TimeSpan timeout = default) where T : struct, IBackendMessage<TdsPacketHeader> => ProtocolReader.Read(this, new T(), timeout);
//     static class ProtocolReader
//     {
//         // As MessageReader is a ref struct we need a small method to create it and pass a reference for the async versions.
//         static ReadStatus ReadCore<TMessage>(ref TMessage message, in ReadOnlySequence<byte> sequence, ref MessageReader<TdsPacketHeader>.ResumptionData resumptionData, ref long consumed, bool resuming) where TMessage: IBackendMessage<TdsPacketHeader>
//         {
//             scoped MessageReader<TdsPacketHeader> reader;
//             if (!resuming)
//                 reader = MessageReader<TdsPacketHeader>.Create(sequence);
//             else if (consumed == 0)
//                 reader = MessageReader<TdsPacketHeader>.Resume(sequence, resumptionData);
//             else
//                 reader = MessageReader<TdsPacketHeader>.Recreate(sequence, resumptionData, consumed);
//
//             var status = message.Read(ref reader);
//             consumed = reader.Consumed;
//             if (status != ReadStatus.Done)
//                 resumptionData = reader.GetResumptionData();
//
//             return status;
//         }
//
//         [MethodImpl(MethodImplOptions.AggressiveInlining)]
//         static int ComputeMinimumSize(long consumed, in MessageReader<TdsPacketHeader>.ResumptionData resumptionData, int maximumMessageChunk)
//         {
//             uint minimumSize = TdsPacketHeader.ByteCount;
//             uint remainingMessage;
//             // If we're in a message but it's consumed we assume the reader wants to read the next header.
//             // Otherwise we'll return either remainingMessage or maximumMessageChunk, whichever is smaller.
//             if (!resumptionData.IsDefault && (remainingMessage = resumptionData.MessageType.Length - resumptionData.MessageIndex) > 0)
//                 minimumSize = remainingMessage < maximumMessageChunk ? remainingMessage : (uint)maximumMessageChunk;
//
//             var result = consumed + minimumSize;
//             if (result > int.MaxValue)
//                 ThrowOverflowException();
//
//             return (int)result;
//
//             static void ThrowOverflowException() => throw new OverflowException("Buffers cannot be larger than int.MaxValue, return ReadStatus.ConsumeData to free data while processing.");
//         }
//
//         static Exception CreateUnexpectedError<T>(TdsProtocol protocol, ReadOnlySequence<byte> buffer, scoped in MessageReader<TdsPacketHeader>.ResumptionData resumptionData, long consumed, Exception? readerException = null)
//         {
//             // Try to read error response.
//             Exception exception;
//             if (readerException is null && !resumptionData.IsDefault && resumptionData.MessageType.Type == BackendCode.ErrorResponse)
//             {
//                 // When we're not Ready yet we're in the startup phase where PG closes the connection without an RFQ.
//                 var errorResponse = new ErrorResponse(protocol.Encoding, expectRfq: protocol.State is not ProtocolState.Created);
//                 Debug.Assert(resumptionData.MessageIndex <= int.MaxValue);
//                 consumed -= resumptionData.MessageIndex;
//                 // Let it start clean, as if it has to MoveNext for the first time.
//                 MessageReader<TdsPacketHeader>.ResumptionData emptyResumptionData = default;
//                 var errorResponseStatus = ReadCore(ref errorResponse, buffer, ref emptyResumptionData, ref consumed, false);
//                 // TODO make this work like a normal message read.
//                 if (errorResponseStatus != ReadStatus.Done)
//                     exception = new Exception($"Unexpected error on message: {typeof(T).FullName}, could not read full error response, terminated connection.");
//                 else
//                     exception = new Exception($"Unexpected error on message: {typeof(T).FullName}, error message: {errorResponse.Message.Message}.");
//             }
//             else
//             {
//                 exception = new Exception($"Protocol desync on message: {typeof(T).FullName}, expected different response{(resumptionData.IsDefault ? "" : ", actual code: " + resumptionData.MessageType.Type)}.", readerException);
//             }
//             return exception;
//         }
//
//         static ValueTask HandleAsyncResponse(in ReadOnlySequence<byte> buffer, scoped ref MessageReader<TdsPacketHeader>.ResumptionData resumptionData, ref long consumed)
//         {
//             // switch (asyncResponseStatus)
//             // {
//             //     case ReadStatus.AsyncResponse:
//             //         throw new Exception("Should never happen, async response handling should not return ReadStatus.AsyncResponse.");
//             //     case ReadStatus.InvalidData:
//             //         throw new Exception("Should never happen, any unknown data during async response handling should be left for the original message handler.");
//             //     case ReadStatus.NeedMoreData:
//             //         _reader.Advance(consumed);
//             //         consumed = 0;
//             //         buffer = isAsync
//             //             ? await ReadAsync(ComputeMinimumSize(resumptionData), cancellationToken.CancellationToken).ConfigureAwait(false)
//             //             : Read(ComputeMinimumSize(resumptionData), cancellationToken.Timeout);
//             //         break;
//             //     case ReadStatus.Done:
//             //         // We don't reset consumed here, the original handler may continue where we left.
//             //         break;
//             // }
//             //
//             // void HandleAsyncResponseCore
//             //
//             var reader = consumed == 0 ? MessageReader<TdsPacketHeader>.Resume(buffer, resumptionData) : MessageReader<TdsPacketHeader>.Recreate(buffer, resumptionData, consumed);
//
//             consumed = (int)reader.Consumed;
//             throw new NotImplementedException();
//         }
//
//         public static T Read<T>(TdsProtocol protocol, T message, TimeSpan timeout = default) where T : IBackendMessage<TdsPacketHeader>
//         {
//             ReadStatus status;
//             MessageReader<TdsPacketHeader>.ResumptionData resumptionData = default;
//             long consumed = 0;
//             Exception? readerExn = null;
//             var readTimeout = timeout != Timeout.InfiniteTimeSpan ? timeout : protocol._protocolOptions.ReadTimeout;
//             var start = TickCount64Shim.Get();
//             var resumed = false;
//             do
//             {
//                 var buffer = protocol._reader.ReadAtLeast(ComputeMinimumSize(consumed, resumptionData, protocol._protocolOptions.MaximumMessageChunkSize), readTimeout);
//
//                 try
//                 {
//                     status = ReadCore(ref message, buffer, ref resumptionData, ref consumed, resumed);
//                 }
//                 catch(Exception ex)
//                 {
//                     // Readers aren't supposed to throw, when we have logging do that here.
//                     status = ReadStatus.InvalidData;
//                     readerExn = ex;
//                 }
//
//                 switch (status)
//                 {
//                     case ReadStatus.Done:
//                     case ReadStatus.ConsumeData:
//                         protocol._reader.Advance(consumed);
//                         consumed = 0;
//                         break;
//                     case ReadStatus.NeedMoreData:
//                         break;
//                     case ReadStatus.InvalidData:
//                         var exception = CreateUnexpectedError<T>(protocol, buffer, resumptionData, consumed, readerExn);
//                         protocol.MoveToComplete(exception, brokenRead: true);
//                         throw exception;
//                     case ReadStatus.AsyncResponse:
//                         protocol._reader.Advance(consumed);
//                         consumed = 0;
//                         HandleAsyncResponse(buffer, ref resumptionData, ref consumed).GetAwaiter().GetResult();
//                         break;
//                 }
//
//                 if (start != -1 && status != ReadStatus.Done)
//                 {
//                     var elapsed = TimeSpan.FromMilliseconds(TickCount64Shim.Get() - start);
//                     readTimeout -= elapsed;
//                 }
//
//                 resumed = true;
//             } while (status != ReadStatus.Done);
//
//             return message;
//         }
//
// #if !NETSTANDARD2_0
//         [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
// #endif
//         public static async ValueTask<T> ReadAsync<T>(TdsProtocol protocol, T message, CancellationToken cancellationToken = default) where T : IBackendMessage<TdsPacketHeader>
//         {
//             ReadStatus status;
//             MessageReader<TdsPacketHeader>.ResumptionData resumptionData = default;
//             long consumed = 0;
//             Exception? readerExn = null;
//             var resuming = false;
//             do
//             {
//                 cancellationToken.ThrowIfCancellationRequested();
//                 var buffer = await protocol._reader.ReadAtLeastAsync(ComputeMinimumSize(consumed, resumptionData, protocol._protocolOptions.MaximumMessageChunkSize), cancellationToken).ConfigureAwait(false);
//
//                 try
//                 {
//                     status = ReadCore(ref message, buffer, ref resumptionData, ref consumed, resuming);
//                 }
//                 catch(Exception ex)
//                 {
//                     // Readers aren't supposed to throw, when we have logging do that here.
//                     status = ReadStatus.InvalidData;
//                     readerExn = ex;
//                 }
//
//                 switch (status)
//                 {
//                     case ReadStatus.Done:
//                     case ReadStatus.ConsumeData:
//                         protocol._reader.Advance(consumed);
//                         consumed = 0;
//                         break;
//                     case ReadStatus.NeedMoreData:
//                         break;
//                     case ReadStatus.InvalidData:
//                         var exception = CreateUnexpectedError<T>(protocol, buffer, resumptionData, consumed, readerExn);
//                         protocol.MoveToComplete(exception, brokenRead: true);
//                         throw exception;
//                     case ReadStatus.AsyncResponse:
//                         protocol._reader.Advance(consumed);
//                         consumed = 0;
//                         await HandleAsyncResponse(buffer, ref resumptionData, ref consumed).ConfigureAwait(false);
//                         break;
//                 }
//
//                 resuming = true;
//             } while (status != ReadStatus.Done);
//
//             return message;
//         }
//     }
//
     async ValueTask WriteInternalAsync<T>(T message, bool flushHint = true, CancellationToken cancellationToken = default) where T : IFrontendMessage
     {
         _flushControl.Initialize();
         try
         {
             await new BatchWriter(this).WriteMessageAsync(message, cancellationToken).ConfigureAwait(false);
             if (_flushControl.WriterCompleted)
                 MoveToComplete();
             else if (flushHint)
                 await _flushControl.FlushAsync(observeFlushThreshold: false, cancellationToken);
         }
         catch (Exception ex) when (!IsTimeoutOrCallerOCE(ex, cancellationToken))
         {
             MoveToComplete(ex);
             throw;
         }
         finally
         {
             if (!IsCompleted)
             {
                 _flushControl.Reset();
             }
         }
     }

     // Throws inlined as this won't be inlined and it's an uncommonly called method.
     public static async ValueTask<TdsProtocol> StartAsync(PipeWriter writer, Stream readStream, SqlServerOptions options, TdsProtocolOptions? protocolOptions = null)
     {
         var conn = new TdsProtocol(writer, readStream, options.Encoding, protocolOptions);
         try
         {
             await conn.WriteInternalAsync(new PreloginMessage(), flushHint: false).ConfigureAwait(false);
             await conn.WriteInternalAsync(new Login7Message(options.Username, options.Password, options.Database, new byte[6])).ConfigureAwait(false);
             await conn._reader.ReadAtLeastAsync(35);
             conn._reader.Advance(35);

             var tokenReader = conn._tokenReader;
             await tokenReader.ReadAndExpectAsync<EnvChangeToken>();
             await tokenReader.ReadAndExpectAsync<InfoToken>();
             await tokenReader.ReadAndExpectAsync<LoginAckToken>();
             await tokenReader.ReadAndExpectAsync<EnvChangeToken>();
             await tokenReader.ReadAndExpectAsync<DoneToken>();

             // Safe to change outside lock, we haven't exposed the instance yet.
             conn._state = ProtocolState.Ready;
             return conn;
         }
         catch (Exception)
         {
             await conn.CompleteAsync();
             throw;
         }
     }

//     // TODO update.
//     // Throws inlined as this won't be inlined and it's an uncommonly called method.
//     public static TdsProtocol Start<TWriter, TReader>(TWriter writer, TReader reader, PgOptions options, TdsProtocolOptions? pipeOptions = null)
//         where TWriter: PipeWriter, ISyncCapablePipeWriter where TReader: PipeReader, ISyncCapablePipeReader
//     {
//         try
//         {
//             var conn = new TdsProtocol(writer, reader, options.Encoding, pipeOptions);
//             conn.WriteMessage(new Startup(options));
//             var msg = conn.ReadMessage(new AuthenticationRequest());
//             switch (msg.AuthenticationType)
//             {
//                 case AuthenticationType.Ok:
//                     conn.ReadMessage<StartupResponses>();
//                     break;
//                 case AuthenticationType.MD5Password:
//                     if (options.Password is null)
//                         throw new InvalidOperationException("No password given, connection expects password.");
//                     conn.WriteMessage(new PasswordMessage(options.Username, options.Password, msg.MD5Salt));
//                     var expectOk = conn.ReadMessage(new AuthenticationRequest());
//                     if (expectOk.AuthenticationType != AuthenticationType.Ok)
//                         throw new Exception("Unexpected authentication response");
//                     conn.ReadMessage<StartupResponses>();
//                     break;
//                 case AuthenticationType.CleartextPassword:
//                 default:
//                     throw new Exception();
//             }
//
//             // Safe to change outside lock, we haven't exposed the instance yet.
//             conn._state = ProtocolState.Ready;
//             return conn;
//         }
//         catch (Exception ex)
//         {
//             writer.Complete(ex);
//             reader.Complete(ex);
//             throw;
//         }
//     }

    void EnqueueUnsynchronized(TdsOperationSource source, CancellationToken cancellationToken)
    {
        if (source.IsExclusiveUse)
            _pendingExclusiveUses++;

        if (!source.IsPooled)
            source.WithCancellationToken(cancellationToken);

        source.BeginWrites(_messageWriteLock, cancellationToken);
        _operations.Enqueue(source);
    }

    public override bool TryStartOperation(OperationSlot slot, OperationBehavior behavior = OperationBehavior.None, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = ThrowIfInvalidSlot(slot, allowUnbound: true, allowActivated: false);

        int count;
        lock (SyncObj)
        {
            if (_state is not ProtocolState.Ready || ((count = _operations.Count) > 0 && behavior.HasImmediateOnly()))
                return false;

            if (behavior.HasExclusiveUse())
                source.IsExclusiveUse = true;
            source.Bind(this);
            EnqueueUnsynchronized(source, cancellationToken);
        }

        if (count == 0)
            source.Activate();

        return true;
    }

    public override bool TryStartOperation([NotNullWhen(true)]out OperationSlot? slot, OperationBehavior behavior = OperationBehavior.None, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TdsOperationSource source;
        lock (SyncObj)
        {
            int count;
            if (_state is not ProtocolState.Ready || ((count = _operations.Count) > 0 && behavior.HasImmediateOnly()))
            {
                slot = null;
                return false;
            }

            var exclusiveUse = behavior.HasExclusiveUse();
            if (count == 0)
            {
                source = exclusiveUse ? _exclusiveOperationSourceSingleton : _operationSourceSingleton;
                source.Reset();
            }
            else
                source = new TdsOperationSource(this, exclusiveUse, pooled: false);

            EnqueueUnsynchronized(source, cancellationToken);
        }

        slot = source;
        return true;
    }

    void CompleteOperation(TdsOperationSource operationSource, Exception? exception)
    {
        TdsOperationSource currentSource;
        bool hasNext = false;
        lock (SyncObj)
        {
            // TODO we may be able to build a linked list instead of needing a queue.
            if (_operations.TryPeek(out currentSource!) && ReferenceEquals(currentSource, operationSource))
            {
                _operations.Dequeue();
                if (!IsCompleted)
                {
                    var result = currentSource.EndWrites(_messageWriteLock);
                    Debug.Assert(result, "Could not end write slot.");
                }
                if (currentSource.IsExclusiveUse)
                    _pendingExclusiveUses--;

                // TODO we must have two states for cancelled and completed, we must transparently consume cancelled commands if anything was written for this slot.
                while ((hasNext = _operations.TryPeek(out currentSource!)) && currentSource.IsCompleted && _operations.TryDequeue(out _))
                {}
            }
        }

        if (exception is not null && _completingException is null)
        {
            _completingException = exception;
            // TODO Mhmmm
            var _ = Task.Factory.StartNew(static state =>
            {
                var items = (Tuple<TdsProtocol, Exception>)state!;
                return items.Item1.CompleteAsync(items.Item2);
            }, Tuple.Create(this, exception), default, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        }

        // Activate the next uncompleted one, outside the lock.
        if (hasNext)
        {
            if (_completingException is not null)
            {
                // Just drain by pushing the last exception down
                while (_operations.TryDequeue(out currentSource!) && !currentSource.IsCompleted)
                {
                    currentSource.TryComplete(new Exception("The connection was previously broken because of the following exception", _completingException));
                }
            }
            else
                currentSource.Activate();
        }
    }

    public override async Task CompleteAsync(Exception? exception = null)
    {
        TdsOperationSource? source;
        lock (SyncObj)
        {
            if (_state is ProtocolState.Draining or ProtocolState.Completed)
                return;

            _state = ProtocolState.Draining;
            source = _operations.Count > 0 ? new TdsOperationSource(this, exclusiveUse: true, pooled: false) : null;
            if (source is not null)
                // Don't enqueue with cancellationtoken, we wait out of band later on.
                // this is to make sure that once we drain we don't stop waiting until we're empty and completed.
                EnqueueUnsynchronized(source, CancellationToken.None);
        }

        Exception? opException = exception;
        try
        {
            if (source is not null)
                await source.Task.AsTask().ConfigureAwait(false);
        }
        catch(Exception ex)
        {
            if (opException is null)
                opException = ex;
        }
        finally
        {
            MoveToComplete(opException);
        }
    }

    public override ProtocolState State => _state;

    // No locks as it doesn't have to be accurate.
    public override bool PendingExclusiveUse => _pendingExclusiveUses != 0;
    public override int Pending => _operations.Count;

    public static OperationSource CreateUnboundOperationSource<TData>(TData data, CancellationToken cancellationToken = default)
        => new TdsOperationSource<TData>(data, null, false, false).WithCancellationToken(cancellationToken);

    public static ref TData GetDataRef<TData>(OperationSlot source)
    {
        if (source is not TdsOperationSource<TData> sourceWithData)
            throw new ArgumentException("This source does not have data, or no data of this type.", nameof(source));

        return ref sourceWithData.Data;
    }

    class TdsOperationSource : OperationSource, IValueTaskSource<Operation>
    {
        // Will be initialized during TakeWriteLock.
        volatile Task? _writeSlot;
        bool _exclusiveUse;

        public TdsOperationSource(TdsProtocol? protocol, bool exclusiveUse, bool pooled)
            : base(protocol, pooled)
        {
            ValueTaskSource.RunContinuationsAsynchronously = false; // TODO see https://github.com/dotnet/runtime/issues/77896
            _exclusiveUse = exclusiveUse;
        }

        TdsProtocol? GetProtocol() => Unsafe.As<Protocol?, TdsProtocol?>(ref Unsafe.AsRef(Protocol));

        public object? InstanceToken => GetProtocol();
        public Task WriteSlot => _writeSlot!;
        public bool IsExclusiveUse
        {
            get => _exclusiveUse;
            internal set
            {
                if (!IsUnbound)
                    ThrowAlreadyBound();

                _exclusiveUse = value;

                static void ThrowAlreadyBound() => throw new InvalidOperationException("Cannot change after binding");
            }
        }

        public bool IsUnbound => !IsCompleted && Protocol is null && !IsPooled;
        public new bool IsPooled => base.IsPooled;
        public new bool IsActivated => base.IsActivated;

        public void Bind(TdsProtocol protocol) => BindCore(protocol);

        public TdsOperationSource WithCancellationToken(CancellationToken cancellationToken)
        {
            AddCancellation(cancellationToken);
            return this;
        }

        public void Activate() => ActivateCore();

        protected override void CompleteCore(Protocol protocol, Exception? exception)
            => Unsafe.As<Protocol, TdsProtocol>(ref protocol).CompleteOperation(this, exception);

        protected override void ResetCore()
        {
            _writeSlot = null;
        }

        // public override ValueTask<Operation> Task => new(_task ??= new ValueTask<Operation>(this, ValueTaskSource.Version).AsTask());
        public override ValueTask<Operation> Task => new(this, ValueTaskSource.Version);

        // TODO ideally we'd take the lock opportunistically, not sure how though as write congruence with queue position is critical.
        public void BeginWrites(SemaphoreSlim writelock, CancellationToken cancellationToken)
        {
            Debug.Assert(_writeSlot is null, "WriteSlot was already set, instance was not properly finished?");
            if (!writelock.Wait(0))
                _writeSlot = writelock.WaitAsync(cancellationToken);
            else
                _writeSlot = System.Threading.Tasks.Task.CompletedTask;
        }

        public bool EndWrites(SemaphoreSlim writelock)
        {
            if (_writeSlot?.IsCompleted == false)
                return false;

            // Null out the completed task before we release.
            var writeSlot = _writeSlot;
            _writeSlot = null!;
            if (writeSlot is not null)
                writelock.Release();
            return true;
        }

        Operation IValueTaskSource<Operation>.GetResult(short token) => ValueTaskSource.GetResult(token);
        ValueTaskSourceStatus IValueTaskSource<Operation>.GetStatus(short token) => ValueTaskSource.GetStatus(token);
        void IValueTaskSource<Operation>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => ValueTaskSource.OnCompleted(continuation, state, token, flags);

        internal new void Reset() => base.Reset();
    }

    class TdsOperationSource<TData> : TdsOperationSource
    {
        TData _data;

        public TdsOperationSource(TData data, TdsProtocol? protocol, bool exclusiveUse, bool pooled) : base(protocol, exclusiveUse, pooled)
        {
            _data = data;
        }

        public ref TData Data => ref _data;
    }

    long _statementCounter;
    readonly Dictionary<Guid, SizedString> _trackedStatements = new();


    /// <summary>
    /// 
    /// </summary>
    /// <param name="statement"></param>
    /// <param name="name"></param>
    /// <returns>True if added, false if it was already added.</returns>
    /// <exception cref="NotImplementedException"></exception>
    public bool GetOrAddStatementName(Statement statement, out SizedString name)
    {
        lock (_trackedStatements)
        {
            if (_trackedStatements.TryGetValue(statement.Id, out name!))
                return false;

            name = statement.Kind switch
            {
                PreparationKind.Auto => new SizedString($"A{++_statementCounter}", Encoding),
                PreparationKind.Command => new SizedString($"C{++_statementCounter}", Encoding),
                PreparationKind.Global => new SizedString($"G{++_statementCounter}", Encoding),
                _ => throw new ArgumentOutOfRangeException()
            };

            _trackedStatements[statement.Id] = name;
            return true;
        }
    }

    public void CloseStatement(Statement statement)
    {
        SizedString name;
        lock (_trackedStatements)
        {
            if (_trackedStatements.TryGetValue(statement.Id, out name))
                _trackedStatements.Remove(statement.Id);
        }

        // TODO enqueue on an event loop that takes care of this, notices, notifications etc.
        if (name.ByteCount is not 0)
            return;
    }

    public bool ContainsStatement(Statement statement)
    {
        lock (_trackedStatements)
        {
            return _trackedStatements.ContainsKey(statement.Id);
        }
    }
}
