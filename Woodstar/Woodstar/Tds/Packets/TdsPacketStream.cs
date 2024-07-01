using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Woodstar.Tds.Packets;

/// <summary>
/// A read-only stream that gets rid of the TDS packet layer.
/// </summary>
public class TdsPacketStream : Stream
{
    readonly Stream _stream;
    byte[] _buf;
    int _pos, _count, _packetRemaining;

    int Remaining => _count - _pos;

    public TdsPacketStream(Stream stream)
    {
        _stream = stream;
        // TODO Should be based on packetsize.
        _buf = new byte[8192];
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var zeroByteRead = buffer.Length == 0;
        var totalCopied = 0;
        var didRead = false;

        while (true)
        {
            var copied = Math.Min(buffer.Length, Math.Min(_packetRemaining, Remaining));
            _buf.AsMemory(_pos, copied).CopyTo(buffer);
            _pos += copied;
            _packetRemaining -= copied;
            totalCopied += copied;

            if (copied == buffer.Length && !zeroByteRead)
                return totalCopied;

            buffer = buffer.Slice(copied);

            if (Remaining is 0)
            {
                if (didRead)
                    return totalCopied;

                _pos = 0;
                var read = await _stream.ReadAsync(_buf, 0, _buf.Length, cancellationToken);
                _count = read;
                if (read is 0)
                    return totalCopied;
                didRead = true;
                if (!zeroByteRead)
                    continue;
                return 0;
            }

            Debug.Assert(_packetRemaining == 0);

            // We're now at the start of a new packet. Make sure we have a full header buffered.
            while (Remaining < TdsPacketHeader.ByteCount)
            {
                Array.Copy(_buf, _pos, _buf, 0, Remaining);
                _count = Remaining;
                _pos = 0;
                var read = await _stream.ReadAsync(_buf, Remaining, _buf.Length - Remaining, cancellationToken);
                _count += read;
                if (read is 0)
                    return totalCopied;
            }

            Debug.Assert(Remaining >= TdsPacketHeader.ByteCount);

            if (!TdsPacketHeader.TryParse(_buf.AsSpan(_pos), out var header))
                throw new InvalidOperationException("Couldn't parse TDS packet header");

            _packetRemaining = header.PacketSize - TdsPacketHeader.ByteCount;
            _pos += TdsPacketHeader.ByteCount;

            if (zeroByteRead && _count > 0)
                return 0;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

    public override void SetLength(long value)
        => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

    public override void Flush()
        => throw new NotSupportedException();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
}
