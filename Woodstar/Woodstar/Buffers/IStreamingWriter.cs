using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Woodstar.Buffers;

// A streaming alternative to a System.IO.Stream, instead based on the preferable IBufferWriter.
interface IStreamingWriter<T>: IBufferWriter<T>
{
    ValueTask FlushAsync(CancellationToken cancellationToken = default);
}

// Need to lift IBufferWriter implementations like Pipes to IStreamingWriter.
class PipeStreamingWriter: IStreamingWriter<byte>
{
    readonly PipeWriter _pipeWriter;
    public PipeStreamingWriter(PipeWriter pipeWriter) => _pipeWriter = pipeWriter;

    public void Advance(int count) => _pipeWriter.Advance(count);
    public Memory<byte> GetMemory(int sizeHint = 0) => _pipeWriter.GetMemory(sizeHint);
    public Span<byte> GetSpan(int sizeHint = 0) => _pipeWriter.GetSpan(sizeHint);

#if !NETSTANDARD2_0
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
#endif
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        // TODO handle flush results.
        var _ = await _pipeWriter.FlushAsync(cancellationToken);
    }
}
