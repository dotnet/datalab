using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Woodstar.Buffers;
using Woodstar.Tds.Packets;

namespace Woodstar.Tds.Messages;

readonly struct SqlBatchMessage: IFrontendMessage
{
    readonly AllHeaders _allHeaders;
    readonly string? _commandText;

    public SqlBatchMessage(AllHeaders allHeaders, string? commandText)
    {
        _allHeaders = allHeaders;
        _commandText = commandText;
    }

    public static TdsPacketHeader MessageType => TdsPacketHeader.CreateType(TdsPacketType.SqlBatch, MessageStatus.ResetConnection);

    public void Write<TWriter>(StreamingWriter<TWriter> writer) where TWriter : IStreamingWriter<byte>
    {
        _allHeaders.Write(writer);
        writer.WriteString(_commandText, Encoding.Unicode);
    }

    public bool CanWriteSynchronously => false;
    public async ValueTask WriteAsync<T>(StreamingWriter<T> writer, CancellationToken cancellationToken = default) where T : IStreamingWriter<byte>
    {
        _allHeaders.Write(writer);
        var value = _commandText;
        const int chunkSize = 8096;
        var offset = 0;
        Encoder? encoder = null;
        while (offset < value.Length)
        {
            var nextLength = Math.Min(chunkSize, value.Length - offset);
            encoder = writer.WriteEncodedResumable(value.AsSpan(offset, nextLength), Encoding.Unicode, encoder);
            offset += nextLength;
            // TODO: Flush, but only if the string doesn't fit in our buffer
            // await writer.FlushAsync(cancellationToken);
        }
    }
}
