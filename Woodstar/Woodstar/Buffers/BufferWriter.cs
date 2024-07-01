// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Woodstar.Buffers;

/// <summary>
/// A fast access struct that wraps <see cref="IBufferWriter{T}"/>.
/// </summary>
/// <typeparam name="T">The type of element to be written.</typeparam>
ref struct BufferWriter<T> where T : IBufferWriter<byte>
{
    /// <summary>
    /// The underlying <see cref="IBufferWriter{T}"/>.
    /// </summary>
    readonly T _output;

    /// <summary>
    /// The result of the last call to <see cref="IBufferWriter{T}.GetMemory(int)"/>, less any bytes already "consumed" with <see cref="Advance(int)"/>.
    /// Backing field for the <see cref="Span"/> property.
    /// </summary>
    Span<byte> _span;

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
    /// <param name="output">The <see cref="IBufferWriter{T}"/> to be wrapped.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BufferWriter(T output)
    {
        BufferedBytes = 0;
        _bytesCommitted = 0;
        _output = output;
        _span = output.GetSpan();
    }

    BufferWriter(T output, Span<byte> activeSpan)
    {
        _output = output;
        _span = activeSpan;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BufferWriter{T}"/> struct.
    /// </summary>
    /// <param name="writer">The existing BufferWriter to be used.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BufferWriter<T> CreateFrom<TWriter>(StreamingWriter<TWriter> writer) where TWriter : IStreamingWriter<byte>, T
        => new(writer.Output, writer.Span)
        {
            BufferedBytes = writer.BufferedBytes,
            _bytesCommitted = writer.BytesCommitted
        };

    public readonly T Output => _output;

    /// <summary>
    /// Gets the result of the last call to <see cref="IBufferWriter{T}.GetSpan(int)"/>.
    /// </summary>
    public readonly Span<byte> Span => _span;

    /// <summary>
    /// Gets the total number of bytes written with this writer.
    /// </summary>
    public readonly long BytesCommitted => _bytesCommitted;

    /// <summary>
    /// Calls <see cref="IBufferWriter{T}.Advance(int)"/> on the underlying writer
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
            _span = _output.GetSpan();
        }
    }

    /// <summary>
    /// Used to indicate that part of the buffer has been written to.
    /// </summary>
    /// <param name="count">The number of bytes written to.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int count)
    {
        BufferedBytes += count;
        _span = _span.Slice(count);
    }

    /// <summary>
    /// Copies the caller's buffer into this writer and calls <see cref="Advance(int)"/> with the length of the source buffer.
    /// </summary>
    /// <param name="source">The buffer to copy in.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(scoped ReadOnlySpan<byte> source)
    {
        if (_span.Length >= source.Length)
        {
            source.CopyTo(_span);
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
        if (_span.Length < count)
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

        _span = _output.GetSpan(count);
    }

    /// <summary>
    /// Copies the caller's buffer into this writer, potentially across multiple buffers from the underlying writer.
    /// </summary>
    /// <param name="source">The buffer to copy into this writer.</param>
    void WriteMultiBuffer(scoped ReadOnlySpan<byte> source)
    {
        while (source.Length > 0)
        {
            if (_span.Length == 0)
            {
                EnsureMore();
            }

            var writable = Math.Min(source.Length, _span.Length);
            source.Slice(0, writable).CopyTo(_span);
            source = source.Slice(writable);
            Advance(writable);
        }
    }
}

// Cannot share the extensions between this and StreamingWriter because ref structs (BufferWriter) can't implement interfaces, not even for the purpose of constrainted calls.
static class BufferWriterExtensions
{
    // Copies whatever is committed to the given writer.
    public static void CopyTo<T, TOutput>(ref this BufferWriter<T> buffer, ref BufferWriter<TOutput> output) where T : ICopyableBuffer<byte>, IBufferWriter<byte> where TOutput : IBufferWriter<byte>
    {
        buffer.Output.CopyTo(output.Output);
        output.Advance((int)buffer.BytesCommitted);
    }

    public static void WriteRaw<T>(ref this BufferWriter<T> buffer, scoped ReadOnlySpan<byte> value) where T : IBufferWriter<byte>
    {
        buffer.Write(value);
    }

    public static void WriteUIntLittleEndian<T>(ref this BufferWriter<T> buffer, uint value)  where T : IBufferWriter<byte>
    {
        buffer.Ensure(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Span, value);
        buffer.Advance(sizeof(uint));
    }

    public static void WriteUShortLittleEndian<T>(ref this BufferWriter<T> buffer, ushort value)  where T : IBufferWriter<byte>
    {
        buffer.Ensure(sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Span, value);
        buffer.Advance(sizeof(ushort));
    }

    public static void WriteUShort<T>(ref this BufferWriter<T> buffer, ushort value)  where T : IBufferWriter<byte>
    {
        buffer.Ensure(sizeof(ushort));
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Span, value);
        buffer.Advance(sizeof(ushort));
    }

    public static void WriteShort<T>(ref this BufferWriter<T> buffer, short value)  where T : IBufferWriter<byte>
    {
        buffer.Ensure(sizeof(short));
        BinaryPrimitives.WriteInt16BigEndian(buffer.Span, value);
        buffer.Advance(sizeof(short));
    }

    public static void WriteInt<T>(ref this BufferWriter<T> buffer, int value) where T : IBufferWriter<byte>
    {
        buffer.Ensure(sizeof(int));
        BinaryPrimitives.WriteInt32BigEndian(buffer.Span, value);
        buffer.Advance(sizeof(int));
    }

    public static void WriteUInt<T>(ref this BufferWriter<T> buffer, uint value) where T : IBufferWriter<byte>
    {
        buffer.Ensure(sizeof(uint));
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Span, value);
        buffer.Advance(sizeof(uint));
    }

    public static void WriteCString<T>(ref this BufferWriter<T> buffer, string value, Encoding encoding, int? encodedLength = null) where T : IBufferWriter<byte>
        => buffer.WriteCString(value.AsSpan(), encoding, encodedLength);

    public static void WriteCString<T>(ref this BufferWriter<T> buffer, ReadOnlySpan<char> value, Encoding encoding, int? encodedLength = null) where T : IBufferWriter<byte>
    {
        buffer.WriteEncoded(value, encoding, encodedLength);
        buffer.WriteByte(0);
    }

    public static void WriteString<T>(ref this BufferWriter<T> buffer, ReadOnlySpan<char> value, Encoding encoding, int? encodedLength = null) where T : IBufferWriter<byte>
    {
        buffer.WriteEncoded(value, encoding, encodedLength);
    }

    public static void WriteByte<T>(ref this BufferWriter<T> buffer, byte b)
        where T : IBufferWriter<byte>
    {
        buffer.Ensure(sizeof(byte));
        buffer.Span[0] = b;
        buffer.Advance(1);
    }

    public static void WriteByte<T>(ref this BufferWriter<T> buffer, sbyte b)
        where T : IBufferWriter<byte>
    {
        buffer.Ensure(sizeof(byte));
        buffer.Span[0] = (byte)b;
        buffer.Advance(1);
    }

    public static void WriteEncoded<T>(ref this BufferWriter<T> buffer, scoped ReadOnlySpan<char> data, Encoding encoding, int? encodedLength = null)
        where T : IBufferWriter<byte>
    {
        if (data.IsEmpty)
            return;

        var dest = buffer.Span;
        var sourceLength = encodedLength ?? encoding.GetByteCount(data);
        // Fast path, try encoding to the available memory directly
        if (sourceLength <= dest.Length)
        {
            encoding.GetBytes(data, dest);
            buffer.Advance(sourceLength);
        }
        else
        {
            WriteEncodedMultiWrite(ref buffer, data, sourceLength, encoding);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void WriteEncodedMultiWrite<T>(ref this BufferWriter<T> buffer, scoped ReadOnlySpan<char> data, int encodedLength, Encoding encoding)
        where T : IBufferWriter<byte>
    {
        var source = data;
        var totalBytesUsed = 0;
        var encoder = encoding.GetEncoder();
        var minBufferSize = encoding.GetMaxByteCount(1);
        buffer.Ensure(minBufferSize);
        var bytes = buffer.Span;
        var completed = false;

        // This may be an underlying problem but encoder.Convert returns completed = true for UTF7 too early.
        // Therefore, we check encodedLength - totalBytesUsed too.
        while (!completed || encodedLength - totalBytesUsed != 0)
        {
            // Zero length spans are possible, though unlikely.
            // encoding.Convert and .Advance will both handle them so we won't special case for them.
            encoder.Convert(source, bytes, flush: true, out var charsUsed, out var bytesUsed, out completed);
            buffer.Advance(bytesUsed);

            totalBytesUsed += bytesUsed;
            if (totalBytesUsed >= encodedLength)
            {
                Debug.Assert(totalBytesUsed == encodedLength);
                // Encoded everything
                break;
            }

            source = source.Slice(charsUsed);

            // Get new span, more to encode.
            buffer.Ensure(minBufferSize);
            bytes = buffer.Span;
        }
    }
}
