using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Woodstar.Pipelines;

/// <summary>
/// PipeReader wrapper that minimizes the cost of advance increments and simplifies forward movement and rereading the same buffer.
/// </summary>
sealed class SimplePipeReader
{
    readonly TimeSpan _readTimeout;
    readonly PipeReader _reader;
    CancellationTokenSource? _readTimeoutSource;
    ReadOnlySequence<byte> _buffer = ReadOnlySequence<byte>.Empty;
    long _bufferLength;
    bool _unfinishedRead;
    long _consumed;
    long _largestMinimumSize;
    bool _completed;
    Exception? _completingException;

    public SimplePipeReader(PipeReader reader, TimeSpan readTimeout)
    {
        _reader = reader;
        _readTimeout = readTimeout;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(long count)
    {
        // Once we pass examined/largest minimum size we reset it to allow the next minimumSize
        // to take effect, as it will all have been consumed at the next read.
        var consumed = _consumed += count;
        if (_largestMinimumSize != 0 && consumed >= _largestMinimumSize)
            _largestMinimumSize = 0;
    }

    /// <summary>Asynchronously reads a sequence of bytes from the current <see cref="System.IO.Pipelines.PipeReader" />.</summary>
    /// <param name="minimumSize">The minimum length that needs to be buffered in order to for the call to return.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is <see langword="default" />.</param>
    /// <returns>A <see cref="System.Threading.Tasks.ValueTask{T}" /> representing the asynchronous read operation.</returns>
    /// <remarks>
    ///     <para>
    ///     The call returns if the <see cref="System.IO.Pipelines.PipeReader" /> has read the minimumLength specified, or is cancelled or completed.
    ///     </para>
    ///     <para>
    ///     Passing a value of 0 for <paramref name="minimumSize" /> will return a <see cref="System.Threading.Tasks.ValueTask{T}" /> that will not complete until
    ///     further data is available. You should instead call <see cref="System.IO.Pipelines.PipeReader.TryRead" /> to avoid a blocking call.
    ///     </para>
    /// </remarks>
    public ValueTask<ReadOnlySequence<byte>> ReadAtLeastAsync(int minimumSize, CancellationToken cancellationToken = default)
    {
        if (!_completed && minimumSize != 0 && _bufferLength - _consumed >= minimumSize)
            return new ValueTask<ReadOnlySequence<byte>>(_buffer.Slice(_consumed));

        return ReadAtLeastAsyncCore(minimumSize, cancellationToken);
    }

#if !NETSTANDARD2_0
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    async ValueTask<ReadOnlySequence<byte>> ReadAtLeastAsyncCore(int minimumsize, CancellationToken cancellationToken)
    {
        if (!_completed && _unfinishedRead)
        {
            HandleAdvanceTo(_consumed, _largestMinimumSize);
            _unfinishedRead = false;
        }

        CancellationTokenSource? timeoutSource = null;
        if (!cancellationToken.CanBeCanceled)
        {
            timeoutSource = _readTimeoutSource ??= new CancellationTokenSource();
            timeoutSource.CancelAfter(_readTimeout);
            cancellationToken = timeoutSource.Token;
        }
        try
        {
            var result = await _reader.ReadAtLeastAsync(minimumsize, cancellationToken).ConfigureAwait(false);
            HandleReadResult(result, minimumsize);
            return result.Buffer;
        }
        catch (OperationCanceledException ex) when (timeoutSource?.Token.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The operation has timed out.", ex);
        }
        finally
        {
            if (timeoutSource is not null && _readTimeoutSource?.TryReset() == false)
            {
                _readTimeoutSource.Dispose();
                _readTimeoutSource = null;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void HandleReadResult(ReadResult result, int minimumSize)
    {
        if (result.IsCompleted || result.IsCanceled)
            HandleUncommon();

        _unfinishedRead = true;
        _buffer = result.Buffer;
        _bufferLength = result.Buffer.Length;
        if (minimumSize > _largestMinimumSize)
            _largestMinimumSize = minimumSize;

        void HandleUncommon()
        {
            if (result.IsCompleted)
            {
                _completed = true;
                throw new InvalidOperationException("Pipe was completed while waiting for more data.", _completingException);
            }

            if (result.IsCanceled)
                throw new OperationCanceledException();
        }
    }

    // See https://github.com/dotnet/runtime/issues/66577, not allowing examined to move back is an absolute shite idea of a usable api.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void HandleAdvanceTo(long consumed, long examined)
    {
        var consumedPosition = _buffer.GetPosition(consumed);
        _reader.AdvanceTo(consumedPosition, examined > consumed ? _buffer.GetPosition(examined) : consumedPosition);
        _bufferLength = 0;
        _consumed = 0;
    }

    public ValueTask CompleteAsync(Exception? exception = null)
    {
        _completed = true;
        _completingException = exception;
        return _reader.CompleteAsync(exception);
    }

    public void Complete(Exception? exception = null)
    {
        _completed = true;
        _completingException = exception;
        _reader.Complete(exception);
    }
}
