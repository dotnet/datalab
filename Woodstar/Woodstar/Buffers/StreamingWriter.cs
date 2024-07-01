using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Woodstar.Buffers;

/// <summary>
/// A fast access class that wraps <see cref="IStreamingWriter{T}"/>.
/// </summary>
/// <typeparam name="TWriter">The type of element to be written.</typeparam>
class StreamingWriter<TWriter> : IStreamingWriter<byte> where TWriter : IStreamingWriter<byte>
{
    /// <summary>
    /// The underlying <see cref="IStreamingWriter{T}"/>.
    /// </summary>
    readonly TWriter _output;

    /// <summary>
    /// The result of the last call to <see cref="IStreamingWriter{T}.GetMemory(int)"/>, less any bytes already "consumed" with <see cref="Advance(int)"/>.
    /// Backing field for the <see cref="Span"/> property.
    /// </summary>
    Memory<byte> _memory;

    /// <summary>
    /// The number of uncommitted bytes (all the calls to <see cref="Advance(int)"/> since the last call to <see cref="Commit"/>).
    /// </summary>
    public int BufferedBytes { get; private set; }

    /// <summary>
    /// The total number of bytes written with this writer.
    /// Backing field for the <see cref="BytesCommitted"/> property.
    /// </summary>
    long _bytesCommitted;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamingWriter{TWriter}"/> struct.
    /// </summary>
    /// <param name="output">The <see cref="IStreamingWriter{T}"/> to be wrapped.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StreamingWriter(TWriter output)
    {
        BufferedBytes = 0;
        _bytesCommitted = 0;
        _output = output;
        _memory = output.GetMemory();
    }

    int RemainingMemory => _memory.Length - BufferedBytes;

    public TWriter Output => _output;

    /// <summary>
    /// Gets the result of the last call to <see cref="IStreamingWriter{T}.GetSpan(int)"/>.
    /// </summary>
    public Span<byte> Span => _memory.Span.Slice(BufferedBytes);

    /// <summary>
    /// Gets the result of the last call to <see cref="IStreamingWriter{T}.GetSpan(int)"/>.
    /// </summary>
    public Memory<byte> Memory => _memory.Slice(BufferedBytes);

    /// <summary>
    /// Gets the total number of bytes written with this writer.
    /// </summary>
    public long BytesCommitted => _bytesCommitted;

    /// <summary>
    /// Calls <see cref="IStreamingWriter{T}.Advance(int)"/> on the underlying writer
    /// with the number of uncommitted bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Commit()
    {
        var buffered = BufferedBytes;
        if (buffered > 0)
        {
            _output.Advance(buffered);
            _bytesCommitted += buffered;
            BufferedBytes = 0;
            _memory = _output.GetMemory();
        }
    }

    /// <summary>
    /// While the returned BufferWriter is used the StreamingWriter should not be written to until CommtiBufferWriter is called.
    /// </summary>
    /// <returns></returns>
    public BufferWriter<TWriter> GetBufferWriter() => BufferWriter<TWriter>.CreateFrom(this);

    // TODO we may be able to be smarter here.
    public void CommitBufferWriter(BufferWriter<TWriter> buffer)
    {
        // We have to commit as we can't go back from a Span to a Memory, so we have to commit to the underlying output so it can resync us.
        buffer.Commit();
        _bytesCommitted = buffer.BytesCommitted;
        BufferedBytes = 0;
        _memory = _output.GetMemory();
    }

    /// <summary>
    /// Used to indicate that part of the buffer has been written to.
    /// </summary>
    /// <param name="count">The number of bytes written to.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int count)
    {
        BufferedBytes += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return Memory;
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return Span;
    }

    /// <summary>
    /// Copies the caller's buffer into this writer and calls <see cref="Advance(int)"/> with the length of the source buffer.
    /// </summary>
    /// <param name="source">The buffer to copy in.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(ReadOnlySpan<byte> source)
    {
        if (RemainingMemory >= source.Length)
        {
            source.CopyTo(Span);
            Advance(source.Length);
        }
        else
        {
            WriteMultiBuffer(source);
        }
    }

    /// <summary>
    /// Acquires a new buffer if necessary to ensure that some given number of bytes can be written to a single buffer.
    /// </summary>
    /// <param name="count">The number of bytes that must be allocated in a single buffer.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Ensure(int count = 1)
    {
        if (RemainingMemory < count)
        {
            EnsureMore(count);
        }
    }

    /// <summary>
    /// Gets a fresh span to write to, with an optional minimum size.
    /// </summary>
    /// <param name="count">The minimum size for the next requested buffer.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    void EnsureMore(int count = 0)
    {
        if (BufferedBytes > 0)
        {
            Commit();
        }

        _memory = _output.GetMemory(count);
    }

    /// <summary>
    /// Copies the caller's buffer into this writer, potentially across multiple buffers from the underlying writer.
    /// </summary>
    /// <param name="source">The buffer to copy into this writer.</param>
    void WriteMultiBuffer(ReadOnlySpan<byte> source)
    {
        while (source.Length > 0)
        {
            if (RemainingMemory == 0)
            {
                EnsureMore();
            }

            var writable = Math.Min(source.Length, RemainingMemory);
            source.Slice(0, writable).CopyTo(Span);
            source = source.Slice(writable);
            Advance(writable);
        }
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken = default) => _output.FlushAsync(cancellationToken);

    protected void Reset()
    {
        _bytesCommitted = 0;
        BufferedBytes = 0;
        _memory = Memory<byte>.Empty;
    }

    public static implicit operator BufferWriter<TWriter>(StreamingWriter<TWriter> writer) => BufferWriter<TWriter>.CreateFrom(writer);
}

class ResettableStreamingWriter<TWriter> : StreamingWriter<TWriter> where TWriter : IStreamingWriter<byte>
{
    public ResettableStreamingWriter(TWriter output) : base(output)
    {
    }

    public new void Reset() => base.Reset();
}

// Cannot share the extensions between this and BufferWriter because ref structs (BufferWriter) can't implement interfaces, not even for the purpose of constrained calls.
static class StreamingWriterExtensions
{
    // Copies whatever is committed to the given writer.
    public static void CopyTo<T, TOutput>(this StreamingWriter<T> writer, ref StreamingWriter<TOutput> output)
        where T : ICopyableBuffer<byte>, IStreamingWriter<byte> where TOutput : IStreamingWriter<byte>
    {
        writer.Output.CopyTo(output);
        output.Advance((int)writer.BytesCommitted);
    }

    public static void WriteRaw<T>(this StreamingWriter<T> writer, ReadOnlySpan<byte> value) where T : IStreamingWriter<byte>
    {
        writer.Write(value);
    }

    public static void WriteUShort<T>(this StreamingWriter<T> writer, ushort value)  where T : IStreamingWriter<byte>
    {
        writer.Ensure(sizeof(short));
        BinaryPrimitives.WriteUInt16BigEndian(writer.Span, value);
        writer.Advance(sizeof(short));
    }

    public static void WriteShort<T>(this StreamingWriter<T> writer, short value) where T : IStreamingWriter<byte>
    {
        writer.Ensure(sizeof(short));
        BinaryPrimitives.WriteInt16BigEndian(writer.Span, value);
        writer.Advance(sizeof(short));
    }

    public static void WriteInt<T>(this StreamingWriter<T> writer, int value) where T : IStreamingWriter<byte>
    {
        writer.Ensure(sizeof(int));
        BinaryPrimitives.WriteInt32BigEndian(writer.Span, value);
        writer.Advance(sizeof(int));
    }

    public static void WriteLong<T>(this StreamingWriter<T> writer, long value) where T : IStreamingWriter<byte>
    {
        writer.Ensure(sizeof(long));
        BinaryPrimitives.WriteInt64BigEndian(writer.Span, value);
        writer.Advance(sizeof(long));
    }

    public static void WriteULong<T>(this StreamingWriter<T> writer, ulong value) where T : IStreamingWriter<byte>
    {
        writer.Ensure(sizeof(long));
        BinaryPrimitives.WriteUInt64BigEndian(writer.Span, value);
        writer.Advance(sizeof(long));
    }

    public static void WriteString<T>(this StreamingWriter<T> writer, string? value, Encoding encoding, int? encodedLength = null) where T : IStreamingWriter<byte>
    {
        writer.WriteEncoded(value, encoding, encodedLength);
    }

    public static void WriteUInt<T>(this StreamingWriter<T> writer, uint value) where T : IStreamingWriter<byte>
    {
        writer.Ensure(sizeof(uint));
        BinaryPrimitives.WriteUInt32BigEndian(writer.Span, value);
        writer.Advance(sizeof(uint));
    }

    public static void WriteUShortLittleEndian<T>(this StreamingWriter<T> writer, ushort value) where T : IStreamingWriter<byte>
    {
        writer.Ensure(sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(writer.Span, value);
        writer.Advance(sizeof(ushort));
    }

    public static void WriteUIntLittleEndian<T>(this StreamingWriter<T> writer, uint value) where T : IStreamingWriter<byte>
    {
        writer.Ensure(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(writer.Span, value);
        writer.Advance(sizeof(uint));
    }

    public static void WriteULongLittleEndian<T>(this StreamingWriter<T> writer, ulong value) where T : IStreamingWriter<byte>
    {
        writer.Ensure(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(writer.Span, value);
        writer.Advance(sizeof(ulong));
    }
    
    public static void WriteCString<T>(this StreamingWriter<T> writer, string value, Encoding encoding, int? encodedLength = null) where T : IStreamingWriter<byte>
        => writer.WriteCString(value.AsSpan(), encoding, encodedLength);

    public static void WriteCString<T>(this StreamingWriter<T> writer, ReadOnlySpan<char> value, Encoding encoding, int? encodedLength = null) where T : IStreamingWriter<byte>
    {
        writer.WriteEncoded(value, encoding, encodedLength);
        writer.WriteByte(0);
    }

    public static void WriteByte<T>(this StreamingWriter<T> writer, byte b) where T : IStreamingWriter<byte>
    {
        writer.Ensure(sizeof(byte));
        writer.Span[0] = b;
        writer.Advance(1);
    }

    public static void WriteEncoded<T>(this StreamingWriter<T> writer, ReadOnlySpan<char> data, Encoding encoding, int? encodedLength = null)
        where T : IStreamingWriter<byte>
    {
        if (data.IsEmpty)
            return;

        var dest = writer.Span;
        var sourceLength = encodedLength ?? encoding.GetByteCount(data);
        // Fast path, try encoding to the available memory directly
        if (sourceLength <= dest.Length)
        {
            encoding.GetBytes(data, dest);
            writer.Advance(sourceLength);
        }
        else
        {
            WriteEncodedMultiWrite(writer, data, sourceLength, encoding);
        }
    }

    public static Encoder? WriteEncodedResumable<T>(this StreamingWriter<T> writer, ReadOnlySpan<char> data, Encoding encoding, Encoder? encoder = null)
        where T : IStreamingWriter<byte>
    {
        if (data.IsEmpty)
            return null;

        var dest = writer.Span;
        var sourceLength = encoding.GetByteCount(data);
        // Fast path, try encoding to the available memory directly
        if (sourceLength <= dest.Length)
        {
            encoding.GetBytes(data, dest);
            writer.Advance(sourceLength);
        }
        else
        {
            return WriteEncodedMultiWrite(writer, data, sourceLength, encoding, encoder);
        }

        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static Encoder? WriteEncodedMultiWrite<T>(this StreamingWriter<T> writer, ReadOnlySpan<char> data, int encodedLength, Encoding encoding, Encoder? enc = null)
        where T : IStreamingWriter<byte>
    {
        var source = data;
        var totalBytesUsed = 0;
        var encoder = enc ?? encoding.GetEncoder();
        var minBufferSize = encoding.GetMaxByteCount(1);
        writer.Ensure(minBufferSize);
        var bytes = writer.Span;
        var completed = false;

        // This may be an underlying problem but encoder.Convert returns completed = true for UTF7 too early.
        // Therefore, we check encodedLength - totalBytesUsed too.
        while (!completed || encodedLength - totalBytesUsed != 0)
        {
            // Zero length spans are possible, though unlikely.
            // encoding.Convert and .Advance will both handle them so we won't special case for them.
            encoder.Convert(source, bytes, flush: true, out var charsUsed, out var bytesUsed, out completed);
            writer.Advance(bytesUsed);

            totalBytesUsed += bytesUsed;
            if (totalBytesUsed >= encodedLength)
            {
                Debug.Assert(totalBytesUsed == encodedLength);
                // Encoded everything
                break;
            }

            source = source.Slice(charsUsed);

            // Get new span, more to encode.
            writer.Ensure(minBufferSize);
            bytes = writer.Span;
        }

        return enc;
    }
}
