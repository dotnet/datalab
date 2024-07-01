using System;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Woodstar.Buffers;

// TODO revisit FlushControl now we have IStreamingWriter, should probably be pushed into it as a wrapper.

readonly struct FlushResult
{
    [Flags]
    enum ResultFlags
    {
        None = 0,
        Canceled = 1,
        Completed = 2,
    }

    readonly ResultFlags _resultFlags;

    public FlushResult(bool isCanceled, bool isCompleted)
    {
        _resultFlags = ResultFlags.None;

        if (isCanceled)
        {
            _resultFlags |= ResultFlags.Canceled;
        }

        if (isCompleted)
        {
            _resultFlags |= ResultFlags.Completed;
        }
    }

    public bool IsCanceled => (_resultFlags & ResultFlags.Canceled) != 0;
    public bool IsCompleted => (_resultFlags & ResultFlags.Completed) != 0;
}

abstract class FlushControl: IDisposable
{
    public abstract TimeSpan FlushTimeout { get; }
    public abstract int FlushThreshold { get; }
    public abstract CancellationToken TimeoutCancellationToken { get; }
    public abstract bool IsFlushBlocking { get; }
    public abstract long UnflushedBytes { get; }
    public abstract ValueTask<FlushResult> FlushAsync(bool observeFlushThreshold = true, CancellationToken cancellationToken = default);
    protected bool _disposed;

    public static FlushControl Create(PipeWriter writer, TimeSpan flushTimeout, int flushThreshold)
    {
        if (!writer.CanGetUnflushedBytes)
            throw new ArgumentException("Cannot accept PipeWriters that don't support UnflushedBytes.", nameof(writer));
        var flushControl = new ResettableFlushControl(writer, flushTimeout, flushThreshold);
        flushControl.Initialize();
        return flushControl;
    }

    public static FlushControl Create(PipeWriter writer, TimeSpan flushTimeout, int flushThreshold, TimeSpan userTimeout)
    {
        if (!writer.CanGetUnflushedBytes)
            throw new ArgumentException("Cannot accept PipeWriters that don't support UnflushedBytes.", nameof(writer));
        var flushControl = new ResettableFlushControl(writer, flushTimeout, flushThreshold);
        flushControl.InitializeAsBlocking(userTimeout);
        return flushControl;
    }

    protected virtual void Dispose(bool disposing)
    {
    }

    public void Dispose()
    {
        _disposed = true;
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected void ThrowIfDisposed()
    {
        if (_disposed)
            ThrowObjectDisposed();

        static void ThrowObjectDisposed() => throw new ObjectDisposedException("FlushControl");
    }
}

class ResettableFlushControl: FlushControl
{
    readonly PipeWriter _writer;
    TimeSpan _userTimeout;
    CancellationTokenSource? _timeoutSource;
    CancellationTokenRegistration? _registration;
    long _start = -2;

    public ResettableFlushControl(PipeWriter writer, TimeSpan flushTimeout, int flushThreshold)
    {
        _writer = writer;
        FlushTimeout = flushTimeout;
        FlushThreshold = flushThreshold;
    }

    public override TimeSpan FlushTimeout { get; }
    public override int FlushThreshold { get; }
    public override CancellationToken TimeoutCancellationToken => _timeoutSource?.Token ?? CancellationToken.None;
    [MemberNotNullWhen(false, nameof(_timeoutSource))]
    public override bool IsFlushBlocking => _timeoutSource is null;
    public override long UnflushedBytes => _writer.UnflushedBytes;

    public bool WriterCompleted { get; private set; }

    TimeSpan GetTimeout()
    {
        if (!IsFlushBlocking)
            throw new InvalidOperationException("Cannot create a token for a non-blocking implementation.");

        if (_start != -1)
        {
            var remaining = _userTimeout - TimeSpan.FromMilliseconds(Environment.TickCount64 - _start);
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException();

            return remaining < FlushTimeout ? remaining : FlushTimeout;
        }

        return FlushTimeout;
    }

    CancellationToken GetToken(CancellationToken cancellationToken)
    {
        if (IsFlushBlocking)
            throw new InvalidOperationException("Cannot create a token for a blocking implementation.");

        cancellationToken.ThrowIfCancellationRequested();
        var flushTimeout = FlushTimeout;
        if (flushTimeout == default || flushTimeout == Timeout.InfiniteTimeSpan)
            return cancellationToken;

        _timeoutSource.CancelAfter(flushTimeout);
        _registration = cancellationToken.UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(), _timeoutSource);
        return _timeoutSource.Token;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void CompleteFlush(System.IO.Pipelines.FlushResult flushResult)
    {
        if (flushResult.IsCompleted)
            WriterCompleted = true;

        if (!IsFlushBlocking)
        {
            _timeoutSource.CancelAfter(Timeout.Infinite);
            _registration?.Dispose();
        }
    }

    public override ValueTask<FlushResult> FlushAsync(bool observeFlushThreshold = true, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (observeFlushThreshold && FlushThreshold != -1 && FlushThreshold > _writer.UnflushedBytes)
            return new ValueTask<FlushResult>(new FlushResult(isCanceled: false, isCompleted: WriterCompleted));

        if (_writer.UnflushedBytes == 0)
            return new ValueTask<FlushResult>(new FlushResult(isCanceled: false, isCompleted: WriterCompleted));

        return Core(this, cancellationToken);

#if !NETSTANDARD2_0
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
        static async ValueTask<FlushResult> Core(ResettableFlushControl instance, CancellationToken cancellationToken)
        {
            System.IO.Pipelines.FlushResult result = default;
            try
            {
                try
                {
                    result = await instance._writer.FlushAsync(instance.GetToken(cancellationToken)).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (instance.TimeoutCancellationToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("The operation has timed out.", ex);
                }

                return new FlushResult(isCanceled: result.IsCanceled, isCompleted: result.IsCompleted);
            }
            finally
            {
                instance.CompleteFlush(result);
            }
        }
    }

    internal void InitializeAsBlocking(TimeSpan timeout)
    {
        ThrowIfDisposed();
        if (IsInitialized)
            throw new InvalidOperationException("Start called before Reset, concurrent use is not supported.");

        _start = _userTimeout.Ticks <= 0 ? -1 : Environment.TickCount64;
        _userTimeout = timeout;
    }

    internal bool IsInitialized => _start != -2;

    internal void Initialize()
    {
        ThrowIfDisposed();
        if (IsInitialized)
            throw new InvalidOperationException("Start called before Reset, concurrent use is not supported.");

        _start = -1;
        _timeoutSource ??= new CancellationTokenSource();
    }

    void ThrowWriterCompleted() => throw new InvalidOperationException("PipeWriter is completed.");

    internal void Reset()
    {
        ThrowIfDisposed();
        if (WriterCompleted)
            ThrowWriterCompleted();
        if (!IsInitialized)
            return;

        _start = -2;
        if (_registration is not null)
        {
            _registration.Value.Dispose();
            _registration = null;
            if (!_timeoutSource!.TryReset())
            {
                _timeoutSource!.Dispose();
                _timeoutSource = null;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        _registration?.Dispose();
        _timeoutSource?.Dispose();
    }
}
