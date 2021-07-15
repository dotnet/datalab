using System;
using System.Buffers;

namespace WoodStar
{
    public class LoginAckStream
    {
        public LoginAckStream(int length, byte majorVersion, byte minorVersion, ushort buildNumber)
        {
            Length = length;
            MajorVersion = majorVersion;
            MinorVersion = minorVersion;
            BuildNumber = buildNumber;
        }

        public int Length { get; }
        public byte MajorVersion { get; }
        public byte MinorVersion { get; }
        public ushort BuildNumber { get; }

        public static LoginAckStream ParseResponse(ReadOnlySequence<byte> buffer)
        {
            var span = buffer.FirstSpan;
            var tokenType = span[0];
            if (tokenType != 0xAD)
            {
                throw new InvalidOperationException();
            }

            var length = span.ReadUshortLittleEndian(1);
            var interface1 = span[3];
            var tdsVersion = span.Slice(4, 4).ToArray();

            return new LoginAckStream(length, 0, 0, 0);
        }
    }
}
