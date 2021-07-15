using System;
using System.Buffers;

namespace WoodStar.Tds;

public sealed class TdsHeader
{
    public const int HeaderSize = 8;

    public TdsHeader(PacketType packetType, PacketStatus packetStatus, int length, int spid, int packetId)
    {
        Type = packetType;
        Status = packetStatus;
        Length = length;
        SPID = spid;
        PacketId = packetId;
    }

    public PacketType Type { get; }
    public PacketStatus Status { get; }
    public int Length { get; } // 2-byte
    public int SPID { get; } // 2-byte
    public int PacketId { get; } // 1-byte
    public byte Window => 0;

    public void Write(Memory<byte> buffer)
    {
        buffer.Span[0] = (byte)Type;
        buffer.Span[1] = (byte)Status;
        buffer.Span[2] = (byte)(Length >> 8);
        buffer.Span[3] = (byte)Length;
        buffer.Span[4] = (byte)(SPID >> 8);
        buffer.Span[5] = (byte)SPID;
        buffer.Span[6] = (byte)PacketId;
        buffer.Span[7] = Window;
    }

    public static TdsHeader Parse(in ReadOnlySequence<byte> sequence)
    {
        var reader = new SequenceReader<byte>(sequence);

        if (reader.TryRead(out var packetType)
            && reader.TryRead(out var packetStatus)
            && reader.TryReadBigEndian(out short length)
            && reader.TryReadBigEndian(out short spid)
            && reader.TryRead(out var packetId))
        {
            return new TdsHeader((PacketType)packetType, (PacketStatus)packetStatus, length, spid, packetId);
        }

        throw new ParsingException();
    }
}
