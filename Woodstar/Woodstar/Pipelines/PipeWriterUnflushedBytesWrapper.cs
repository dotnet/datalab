using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Woodstar.Pipelines;

// Needed to use UnflushedBytes on: https://github.com/mgravell/Pipelines.Sockets.Unofficial/blob/751df8cce767f2ec9e91dd0eb674b9d091f53c20/src/Pipelines.Sockets.Unofficial/SocketConnection.cs#L412
sealed class PipeWriterUnflushedBytesWrapper: PipeWriter
{
    readonly PipeWriter _pipeWriter;
    long _bytesBuffered;

    public PipeWriterUnflushedBytesWrapper(PipeWriter writer)
    {
        _pipeWriter = writer;
    }

    public override bool CanGetUnflushedBytes => true;
    public override long UnflushedBytes => _bytesBuffered;

    public override ValueTask CompleteAsync(Exception? exception = null) => _pipeWriter.CompleteAsync(exception);
    public override void Complete(Exception? exception = null) => _pipeWriter.Complete();

    public override void CancelPendingFlush() => _pipeWriter.CancelPendingFlush();

    public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        var result = await _pipeWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
        _bytesBuffered = 0;
        return result;
    }

    public override void Advance(int bytes)
    {
        _pipeWriter.Advance(bytes);
        _bytesBuffered += bytes;
    }

    public override Memory<byte> GetMemory(int sizeHint = 0) => _pipeWriter.GetMemory(sizeHint);
    public override Span<byte> GetSpan(int sizeHint = 0) => _pipeWriter.GetSpan(sizeHint);
}
