using System;
using System.Buffers;
using System.IO;
using System.Threading.Tasks;

namespace WoodStar.Tds.Streams;

public class SqlBatchStream : ITdsStream
{
    private readonly int _length;

    public SqlBatchStream(AllHeaders headers, string sqlText)
    {
        Headers = headers;
        SqlText = sqlText;
        // TODO: Types
        _length = (int)headers.Length + 2 * sqlText.Length;
    }

    public AllHeaders Headers { get; }
    public string SqlText { get; }

    public async Task SendPacket(Stream stream, bool resetConnection)
    {
        var length = _length;
        var packetId = 1;
        var memoryOwner = MemoryPool<byte>.Shared.Rent(Math.Min(length, 4096));
        while (length != 0)
        {
            if (length < 4088)
            {
                var packetLength = length + TdsHeader.HeaderSize;
                var status = PacketStatus.EOM;
                if (resetConnection)
                {
                    status |= PacketStatus.ResetConnection;
                }
                var header = new TdsHeader(PacketType.SQLBatch, status, packetLength, spid: 0, packetId);
                var buffer = memoryOwner.Memory;
                header.Write(buffer);
                Write(buffer[8..]);
                var a = HelperMethods.PrintBuffer(buffer.ToArray(), header.Length);
                await stream.WriteAsync(buffer[..packetLength]);

                length -= length;
            }
            else
            {
                // TODO: when the stream is longer than 4088 bytes
                throw new NotImplementedException();
            }
        }

        memoryOwner.Dispose();
    }

    private void Write(Memory<byte> buffer)
    {
        Headers.Write(buffer);
        buffer = buffer[(int)Headers.Length..];

        for (var i = 0; i < SqlText.Length; i++)
        {
            buffer.Span[2 * i] = (byte)SqlText[i];
            buffer.Span[2 * i + 1] = 0;
        }
    }
}
