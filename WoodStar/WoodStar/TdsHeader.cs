using System;
using System.Buffers;

namespace WoodStar
{
    public class TdsHeader
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

        public void WriteToBuffer(byte[] buffer)
        {
            var i = 0;
            buffer[i++] = (byte)Type;
            buffer[i++] = (byte)Status;
            buffer[i++] = (byte)(Length >> 8);
            buffer[i++] = (byte)Length;
            buffer[i++] = (byte)(SPID >> 8);
            buffer[i++] = (byte)SPID;
            buffer[i++] = (byte)PacketId;
            buffer[i++] = Window;
        }

        public static TdsHeader Parse(ReadOnlySequence<byte> buffer)
        {
            var reader = new SequenceReader<byte>(buffer);
            if (reader.Length < HeaderSize)
            {
                throw new InvalidOperationException();
            }

            reader.TryRead(out var packetType);
            reader.TryRead(out var packetStatus);
            reader.TryReadBigEndian(out short packetLength);
            reader.TryReadBigEndian(out short spid);
            reader.TryRead(out var packetId);

            return new TdsHeader((PacketType)packetType, (PacketStatus)packetStatus, packetLength, spid, packetId);
        }
    }
}
