using System;
using Woodstar.Buffers;
using Woodstar.Tds.Packets;
using Xunit;

namespace Woodstar.Tests;

public class DataStreamWriterTests
{
    const short PacketSize = 1233; // Arbitrary awkward number (we could property test this too).
    const short PayloadSize = PacketSize - TdsPacketHeader.ByteCount;
    const short SegmentSize = 4096;
    const byte PayloadData = 0xFF;

    public DataStreamWriterTests()
    {
        Writer = new TdsPacketWriter(Buffer, PacketSize);
    }

    MemoryBufferWriter Buffer { get; } = new(minimumSegmentSize: SegmentSize);
    TdsPacketWriter Writer { get; }


    [Fact]
    void ThrowOnEndOfMessageStatus()
    {
        Assert.Throws<ArgumentException>(() => Writer.StartMessage(TdsPacketType.PreLogin, MessageStatus.EndOfMessage));
    }

    [Fact]
    void OneShotWriteMessage()
    {
        const int packets = (SegmentSize + (PacketSize - 1)) / PacketSize;
        const int lastPacketSize = SegmentSize % PacketSize;
        const int adjustedLength = SegmentSize - TdsPacketHeader.ByteCount * packets;

        Writer.StartMessage(TdsPacketType.PreLogin, MessageStatus.Normal);
        var span = Writer.GetSpan(adjustedLength);
        Assert.Equal(span.Length, adjustedLength);
        span.Fill(PayloadData);
        Writer.Advance(span.Length, endMessage: true);

        VerifyData(packets, lastPacketSize, TdsPacketType.PreLogin, MessageStatus.Normal);
    }

    [Fact]
    void MultiWriteMessageTwoPacketsExact()
    {
        const int packets = 2;
        const int lastPacketSize = PacketSize;

        Writer.StartMessage(TdsPacketType.SqlBatch, MessageStatus.Normal);
        var span = Writer.GetSpan();
        span.Slice(0, PayloadSize).Fill(PayloadData);
        Writer.Advance(PayloadSize);

        span = Writer.GetSpan(PayloadSize);
        span.Slice(0, PayloadSize).Fill(PayloadData);
        Writer.Advance(PayloadSize, endMessage: true);

        VerifyData(packets, lastPacketSize, TdsPacketType.SqlBatch, MessageStatus.Normal);
    }

    [Fact]
    void MultiWriteMessageOnePacket()
    {
        const int packets = 1;
        const int lastPacketSize = 908;
        const int chunkSize = 300;

        Writer.StartMessage(TdsPacketType.PreLogin, MessageStatus.Normal);
        // Fill one chunk.
        var span = Writer.GetSpan();
        span.Slice(0, chunkSize).Fill(PayloadData);
        Writer.Advance(chunkSize);

        span = Writer.GetSpan();
        span.Slice(0, chunkSize).Fill(PayloadData);
        Writer.Advance(chunkSize);

        span = Writer.GetSpan();
        span.Slice(0, chunkSize).Fill(PayloadData);
        Writer.Advance(chunkSize, endMessage: true);

        VerifyData(packets, lastPacketSize, TdsPacketType.PreLogin, MessageStatus.Normal);
    }

    [Fact]
    void MultiWriteMessageOnePacketMultiBuffer()
    {
        const int packets = 1;
        const int chunkSize = PayloadSize / 4;
        const int lastPacketSize = chunkSize * 4 + TdsPacketHeader.ByteCount;

        Writer.StartMessage(TdsPacketType.Rpc, MessageStatus.Normal);
        // Fill one chunk.
        var span = Writer.GetSpan();
        span.Slice(0, chunkSize).Fill(PayloadData);
        Writer.Advance(chunkSize);

        // This should return our scratch buffer (which should never be larger than a packet payload and then some).
        span = Writer.GetSpan();
        var remainingPayloadSpace = PayloadSize - chunkSize;
        Assert.InRange(span.Length, remainingPayloadSpace, remainingPayloadSpace * 2);
        span.Slice(0, chunkSize).Fill(PayloadData);
        Writer.Advance(chunkSize);

        // Force scratch buffer to be copied into a requested buffer that's larger than it is.
        var scratchSize = span.Length;
        span = Writer.GetSpan(scratchSize + 1);
        Assert.InRange(span.Length, scratchSize + 1, int.MaxValue);
        span.Slice(0, chunkSize).Fill(PayloadData);
        Writer.Advance(chunkSize);

        span = Writer.GetSpan();
        span.Slice(0, chunkSize).Fill(PayloadData);
        Writer.Advance(chunkSize);

        // Advance by zero should still finish the message.
        Writer.Advance(0, endMessage: true);

        VerifyData(packets, lastPacketSize, TdsPacketType.Rpc, MessageStatus.Normal);
    }

    void VerifyData(int packets, int expectLastPacketSize, TdsPacketType type, MessageStatus status)
    {
        if (status.HasFlag(MessageStatus.EndOfMessage))
            throw new ArgumentException();
        if (!Enum.IsDefined(type))
            throw new ArgumentOutOfRangeException();

        var packetStart = 0;
        var data = Buffer.ToArray().AsSpan();
        for (var packet = 0; packet < packets; packet++)
        {
            Assert.True(TdsPacketHeader.TryParse(data.Slice(packetStart), out var header));
            Assert.Equal(type, header.Type);
            Assert.Equal(packet + 1, header.PacketId);
            if (packet != packets - 1)
            {
                Assert.Equal(status, header.Status);
                Assert.Equal(PacketSize, header.PacketSize);
            }
            else
            {
                Assert.Equal(status | MessageStatus.EndOfMessage, header.Status);
                Assert.Equal(expectLastPacketSize, header.PacketSize);
            }

            // And also check that we didn't lose any payload data.
            Assert.True(data.Slice(packetStart + TdsPacketHeader.ByteCount, header.PacketSize - TdsPacketHeader.ByteCount).IndexOfAnyExcept(PayloadData) == -1);

            packetStart += PacketSize;
        }
    }
}
