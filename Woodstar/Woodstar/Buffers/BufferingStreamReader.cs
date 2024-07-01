using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Woodstar.Buffers;

class BufferingStreamReader
{
    readonly Stream _stream;
    readonly byte[] _buf;
    int _pos;
    int _count;

    public BufferingStreamReader(Stream stream, int bufferSize = 8192)
    {
        _stream = stream;
        _buf = new byte[bufferSize];
    }

    int Remaining => _count - _pos;

    public void Advance(int count)
    {
        if (count > Remaining)
            throw new ArgumentOutOfRangeException(nameof(count));

        _pos += count;
    }

    public ValueTask<BufferReader> ReadAtLeastAsync(int minimumSize, CancellationToken cancellationToken = default)
    {
        if (minimumSize <= Remaining)
            return new(new BufferReader(this, _buf, _pos, _count));

        return Core(minimumSize, cancellationToken);

        async ValueTask<BufferReader> Core(int minimumSize, CancellationToken cancellationToken = default)
        {
            if (minimumSize > _buf.Length)
                throw new ArgumentOutOfRangeException(nameof(minimumSize));
            Debug.Assert(minimumSize > Remaining);

            if (minimumSize > Remaining)
            {
                Array.Copy(_buf, _pos, _buf, 0, Remaining);
                _count = Remaining;
                _pos = 0;
            }

            _count += await _stream.ReadAtLeastAsync(
                _buf.AsMemory(Remaining), minimumSize - Remaining, throwOnEndOfStream: true, cancellationToken);

            return new BufferReader(this, _buf, _pos, _count);
        }
    }
}

struct BufferReader
{
    readonly BufferingStreamReader _reader;
    readonly byte[] _buf;
    int _start, _pos;
    int _count;

    public BufferReader(BufferingStreamReader reader, byte[] buffer, int start, int count)
    {
        _reader = reader;
        _buf = buffer;
        _start = _pos = start;
        _count = count;
    }

    public static BufferReader Empty => new(null!, Array.Empty<byte>(), 0, 0);

    public int Remaining => _count - _pos;
    public int Consumed => _pos - _start;

    public ReadOnlySpan<byte> UnreadSpan => _buf.AsSpan(_pos);

    public void Advance(int length)
    {
        if (Remaining < length)
            throw new ArgumentOutOfRangeException();
        _pos += length;
    }

    public void Commit()
    {
        _reader.Advance(Consumed);
        _start = _pos;
    }

    public bool TryRead(out byte value)
    {
        var span = _buf.AsSpan(_pos);
        if (span.Length == 0)
        {
            value = default;
            return false;
        }

        value = span[0];
        Advance(sizeof(byte));
        return true;
    }

    public bool TryReadLittleEndian(out ushort value)
    {
        var span = _buf.AsSpan(_pos);
        if (span.Length < sizeof(ushort))
        {
            value = default;
            return false;
        }
        Advance(sizeof(ushort));
        return BinaryPrimitives.TryReadUInt16LittleEndian(span, out value);
    }

    public bool TryReadLittleEndian(out int value)
    {
        var span = _buf.AsSpan(_pos);
        if (span.Length < sizeof(int))
        {
            value = default;
            return false;
        }
        Advance(sizeof(int));
        return BinaryPrimitives.TryReadInt32LittleEndian(span, out value);
    }

    public bool TryReadLittleEndian(out uint value)
    {
        var span = _buf.AsSpan(_pos);
        if (span.Length < sizeof(uint))
        {
            value = default;
            return false;
        }
        Advance(sizeof(uint));
        return BinaryPrimitives.TryReadUInt32LittleEndian(span, out value);
    }

    public bool TryReadLittleEndian(out long value)
    {
        var span = _buf.AsSpan(_pos);
        if (span.Length < sizeof(long))
        {
            value = default;
            return false;
        }
        Advance(sizeof(long));
        return BinaryPrimitives.TryReadInt64LittleEndian(span, out value);
    }

    public bool TryReadLittleEndian(out ulong value)
    {
        var span = _buf.AsSpan(_pos);
        if (span.Length < sizeof(ulong))
        {
            value = default;
            return false;
        }
        Advance(sizeof(ulong));
        return BinaryPrimitives.TryReadUInt64LittleEndian(span, out value);
    }

    public bool TryReadBVarchar([NotNullWhen(true)] out string? value, [NotNullWhen(false)]out int? totalByteLength)
    {
        if (TryRead(out var len) && len < Remaining)
        {
            totalByteLength = null;
            if (len > 0)
            {
                var sequence = _buf.AsSpan(_pos, 2 * len);
                value = Encoding.Unicode.GetString(sequence);
                Advance(2 * len);
                return true;
            }

            value = "";
            return true;
        }

        value = null;
        totalByteLength = len + sizeof(ushort);
        return false;
    }

    public bool TryReadUsVarchar([NotNullWhen(true)] out string? value, [NotNullWhen(false)]out int? totalByteLength)
    {
        if (TryReadLittleEndian(out ushort len) && len < Remaining)
        {
            totalByteLength = null;
            if (len > 0)
            {
                var sequence = _buf.AsSpan(_pos, 2 * len);
                value = Encoding.Unicode.GetString(sequence);
                Advance(2 * len);
                return true;
            }

            value = "";
            return true;
        }

        value = null;
        totalByteLength = len + sizeof(ushort);
        return false;
    }

    public bool TryReadTo(Span<byte> destination)
    {
        if (TryCopyTo(destination))
        {
            Advance(destination.Length);
            return true;
        }

        return false;
    }

    public bool TryCopyTo(Span<byte> destination)
    {
        if (Remaining < destination.Length)
            return false;

        _buf.AsSpan(_pos, Math.Min(destination.Length, Remaining)).CopyTo(destination);
        return true;
    }
}

