using System;
using System.Buffers;
using System.Text;
using Woodstar.Buffers;

namespace Woodstar;

// 'Unsafe' helper struct that pairs a string with some encoding's bytecount.
// It's up to the user to make sure these values match and that the encoding used to write out the string is the expected one.
readonly struct SizedString
{
    readonly string _value;
    readonly int _byteCount;

    public SizedString(string value)
    {
        _value = value;
        _byteCount = value.Length is 0 ? 0 : -1;
    }

    public SizedString(string value, Encoding encoding)
    {
        _value = value;
        _byteCount = encoding.GetByteCount(value);
    }

    public int? ByteCount
    {
        get => _byteCount == -1 ? null : _byteCount;
        init
        {
            if (value is null)
                throw new ArgumentNullException(nameof(value));
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));

            _byteCount = value.GetValueOrDefault();
        }
    }

    public string Value => _value;
    public bool IsDefault => _value is null;

    public SizedString WithEncoding(Encoding encoding)
        => this with { ByteCount = encoding.GetByteCount(_value) };

    public SizedString EnsureByteCount(Encoding encoding)
        => ByteCount is null ? WithEncoding(encoding) : this;

    public static SizedString Empty => new(string.Empty);
    public static explicit operator SizedString(string value) => new(value);
    public static implicit operator string(SizedString value) => value.Value;
}

static class BufferWriterExtensions
{
    public static void WriteEncoded<T>(ref this BufferWriter<T> buffer, SizedString value, Encoding encoding) where T : IBufferWriter<byte>
        => buffer.WriteEncoded(value.Value.AsSpan(), encoding, value.ByteCount);

    public static void WriteCString<T>(ref this BufferWriter<T> buffer, SizedString value, Encoding encoding) where T : IBufferWriter<byte>
        => buffer.WriteCString(value.Value.AsSpan(), encoding, value.ByteCount);
}

static class StreamingWriterExtensions
{
    public static void WriteEncoded<T>(this StreamingWriter<T> writer, SizedString value, Encoding encoding) where T : IStreamingWriter<byte>
        => writer.WriteEncoded(value.Value.AsSpan(), encoding, value.ByteCount);

    public static void WriteCString<T>(this StreamingWriter<T> writer, SizedString value, Encoding encoding) where T : IStreamingWriter<byte>
        => writer.WriteCString(value.Value.AsSpan(), encoding, value.ByteCount);
}
