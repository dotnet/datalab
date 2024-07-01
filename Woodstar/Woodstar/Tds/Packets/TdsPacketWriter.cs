using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using Woodstar.Buffers;

namespace Woodstar.Tds.Packets;

sealed class TdsPacketWriter: IStreamingWriter<byte>
{
    // This is the size given by the Login7 response and must be respected.
    readonly short _packetSize;
    readonly IStreamingWriter<byte> _writer;

    Memory<byte> _activeBuffer;

    bool _messageCompleted;
    int _scratchBufferCount;
    byte[]? _scratchBuffer;
    bool _scratchBufferActive;
    byte _packetId;
    TdsPacketType _tdsPacketType;
    MessageStatus _messageStatus;

    public TdsPacketWriter(IStreamingWriter<byte> writer, short packetSize)
    {
        _writer = writer;
        _packetSize = packetSize;
        _packetId = 0;
        _messageCompleted = true;
    }

    short MaxPayloadSize => (short)(_packetSize - TdsPacketHeader.ByteCount);
    int ScratchBufferSize => (int)(MaxPayloadSize * 1.10); // We add 10% to be sure we can easily handle a full packet + some spillage.

    byte[] EnsureScratchBuffer()
    {
        if (_scratchBuffer is { } buffer)
            return buffer;

        return _scratchBuffer = ArrayPool<byte>.Shared.Rent(ScratchBufferSize);
    }

    void ReturnScratchBuffer()
    {
        // We could add configuration whether we want to return it every time (lower mem usage per conn) or just allocate a long living array.
        ArrayPool<byte>.Shared.Return(_scratchBuffer!);
    }

    void ThrowIfNotStarted()
    {
        if (_messageCompleted)
            throw new InvalidOperationException("No message was started.");
    }

    public IStreamingWriter<byte> StartMessage(TdsPacketType type, MessageStatus status)
    {
        if (!_messageCompleted)
            throw new InvalidOperationException("Previous message was not completed yet.");
        if (status.HasFlag(MessageStatus.EndOfMessage))
            throw new ArgumentException($"Invalid to specify {nameof(MessageStatus.EndOfMessage)}, this is handled by the message writer.");

        _messageCompleted = false;
        _tdsPacketType = type;
        _messageStatus = status;
        return this;
    }

    void ResetMessage()
    {
        _tdsPacketType = default;
        _messageStatus = default;
        _messageCompleted = true;
        if (_scratchBufferCount > 0)
        {
            _scratchBufferActive = false;
            _scratchBufferCount = 0;
            ReturnScratchBuffer();
        }
        // _packetId = 0;
    }

    public void EndMessage()
        => Advance(0, endMessage: true);

    // TODO when there is just one unfinished packet we don't have to copy into scratch but can just withold the advance, more bookkeeping though.
    public void Advance(int count, bool endMessage = false)
    {
        ThrowIfNotStarted();

        // Account for any scratch buffer use that was copied in during GetMemory/Span as it's in use but invisible to the user.
        count += _scratchBufferCount;
        Span<byte> data;
        if (_scratchBufferActive)
        {
            _scratchBufferCount = count;
            // If we handed out the scratch buffer, we're not ending the message, and the packet is not complete then just return.
            if (!endMessage && count < MaxPayloadSize)
                return;

            data = _writer.GetSpan(count);
            _scratchBuffer.AsSpan(0, count).CopyTo(data.Slice(TdsPacketHeader.ByteCount));
        }
        else
            data = _activeBuffer.Span;

        var packetType = _tdsPacketType;
        var messageStatus = _messageStatus;
        if (endMessage)
            ResetMessage();

        var packetCount = GetPacketCount(count);

        var actualBytes = 0;
        var maxPayloadSize = MaxPayloadSize;
        var lastPacketId = (byte)((_packetId + packetCount) % 256);
        var remainderPayloadSize = (short)(count % maxPayloadSize);
        // If we have a remainder to store in the scratch buffer we will send one less packet.
        if (remainderPayloadSize is not 0 && !endMessage)
            lastPacketId--;
        var packetId = lastPacketId;
        // We're processing from the end to reduce copy/shifting work.
        for (var processedPackets = 0; processedPackets < packetCount; processedPackets++)
        {
            var remainingPackets = packetCount - 1 - processedPackets;
            // If it's the first packet we take data as the start directly.
            var packetStartSpan = remainingPackets is not 0 ? data.Slice(TdsPacketHeader.ByteCount + remainingPackets * maxPayloadSize) : data;
            var actualPayloadSize = processedPackets is 0 && remainderPayloadSize is not 0 ? remainderPayloadSize : maxPayloadSize;

            // This will be a continued packet, we must store its contents and return it or copy it to the next buffer in GetMemory/Span, depending on sizeHint.
            if (processedPackets is 0 && remainderPayloadSize is not 0 && !endMessage)
            {
                // First packet should skip its prepended header space.
                packetStartSpan.Slice(remainingPackets is 0 ? TdsPacketHeader.ByteCount : 0, remainderPayloadSize).CopyTo(EnsureScratchBuffer());
                _scratchBufferCount = remainderPayloadSize;
            }
            else
            {
                var actualPacketSize = (short)(actualPayloadSize + TdsPacketHeader.ByteCount);
                // If it's the first packet we have prepended header space so we can skip the data shift work.
                // Otherwise bound to the payload size and shift it by the remaining amount of headers which were reserved at the end.
                if (remainingPackets is not 0)
                {
                    var newDataStart = TdsPacketHeader.ByteCount * remainingPackets;
                    packetStartSpan.Slice(0, actualPayloadSize).CopyTo(packetStartSpan.Slice(newDataStart));
                    packetStartSpan = packetStartSpan.Slice(newDataStart - TdsPacketHeader.ByteCount);
                }

                var status = processedPackets is 0 && endMessage ? messageStatus | MessageStatus.EndOfMessage : messageStatus;
                TdsPacketHeader.Create(packetType, status, actualPacketSize, packetId).Write(packetStartSpan);
                actualBytes += actualPacketSize;
                packetId--;
            }
        }

        _activeBuffer = default;
        _packetId = lastPacketId;
        _writer.Advance(actualBytes);
    }

    void IBufferWriter<byte>.Advance(int count)
        => Advance(count, endMessage: false);

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        var scratchBufferCount = _scratchBufferCount;
        // When there is enough space in the scratch buffer we can use this as the buffer we return.
        if (scratchBufferCount > 0 && scratchBufferCount < ScratchBufferSize && sizeHint <= ScratchBufferSize - scratchBufferCount)
        {
            _scratchBufferActive = true;
            return _scratchBuffer.AsMemory(scratchBufferCount, ScratchBufferSize - scratchBufferCount);
        }

        _scratchBufferActive = false;
        sizeHint += scratchBufferCount;
        var packetCount = GetPacketCount(sizeHint);
        var sizeOfHeaders = packetCount * TdsPacketHeader.ByteCount;
        var mem = _activeBuffer = _writer.GetMemory(sizeHint + sizeOfHeaders);
        packetCount = GetPacketCount(mem.Length);

        // If we couldn't use our buffer we must copy it into the requested buffer.
        if (scratchBufferCount > 0)
            _scratchBuffer.AsMemory(0, scratchBufferCount).CopyTo(mem.Slice(TdsPacketHeader.ByteCount));

        // We'll leave empty space for one header at the start of the buffer as this speeds up simple single packet cases given there is no need to shift data.
        // We'll cut off the rest of the potential headers from the end to reserve enough space for any buffer advance by the user.
        // During advance we'll copy it all into the right places before advancing the underlying writer.
        return mem.Slice(TdsPacketHeader.ByteCount + scratchBufferCount, mem.Length - scratchBufferCount - TdsPacketHeader.ByteCount * packetCount);
    }

    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
        => _writer.FlushAsync(cancellationToken);

    int GetPacketCount(int size)
    {
        if (size <= _packetSize)
            return 1;

        return (size + (_packetSize - 1)) / _packetSize;
    }
}
