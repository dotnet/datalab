using System;
using System.Buffers;

namespace WoodStar
{
    public class TdsHeader
    {
        public static int HeaderSize = 8;

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
            // TODO: Use sequence reader.
            if (buffer.Length != 8
                || !buffer.IsSingleSegment)
            {
                throw new InvalidOperationException();
            }

            var span = buffer.FirstSpan;
            return new TdsHeader(
                (PacketType)span[0], (PacketStatus)span[1], ConvertBytes(span[2], span[3]), ConvertBytes(span[4], span[5]), span[6]);
        }

        private static int ConvertBytes(byte h, byte l)
        {
            return h * 0xFF + l;
        }
    }
}
