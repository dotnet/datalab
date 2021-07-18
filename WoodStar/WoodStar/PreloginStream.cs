using System;
using System.Buffers;

namespace WoodStar
{
    public class PreloginStream
    {
        private const int NumberOfOptions = 7;
        private const int SizePerOption = 5; // Option(1) + Offset(2) + Length(2)

        public PreloginStream(
            byte majorVersion,
            byte minorVersion,
            ushort buildNumber,
            ushort subBuildNumber,
            EncryptionOption encryptionOption,
            string? instanceName,
            uint threadId,
            bool mars,
            Guid? connectionId,
            Guid? activityId,
            uint? activitySequence)
        {
            MajorVersion = majorVersion;
            MinorVersion = minorVersion;
            BuildNumber = buildNumber;
            SubBuildNumber = subBuildNumber;
            EncryptionOption = encryptionOption;
            InstanceName = instanceName;
            ThreadId = threadId;
            Mars = mars;
            ConnectionId = connectionId;
            ActivityId = activityId;
            ActivitySequence = activitySequence;
        }

        public byte MajorVersion { get; }
        public byte MinorVersion { get; }
        public ushort BuildNumber { get; }
        public ushort SubBuildNumber { get; }
        public EncryptionOption EncryptionOption { get; }
        public string? InstanceName { get; }
        public uint ThreadId { get; }
        public bool Mars { get; }
        public Guid? ConnectionId { get; }
        public Guid? ActivityId { get; }
        public uint? ActivitySequence { get; }

        public int Length
            => NumberOfOptions * SizePerOption + 1 /* Terminator */
                + 6 /* Version */
                + 1 /* Encryption */
                + 1 + (string.IsNullOrEmpty(InstanceName) ? 0 : InstanceName.Length)
                + 4 /* ThreadId */
                + 1 /* MARS */
                + (ConnectionId == null ? 0 : 36) /* ConnectionIdActivityIdActivitySequence */
                + 1 /* FedAuth */;

        public void WriteToBuffer(byte[] buffer)
        {
            var headerSize = TdsHeader.HeaderSize;
            var bufferOffset = headerSize;
            var payloadOffset = (ushort)(headerSize + NumberOfOptions * SizePerOption + 1);

            for (var i = 0; i < NumberOfOptions; i++)
            {
                buffer[bufferOffset++] = (byte)i;
                buffer.WriteUnsignedShortBigEndian(bufferOffset, (ushort)(payloadOffset - headerSize));
                bufferOffset += 2;
                ushort length = 0;
                switch (i)
                {
                    case 0:
                        // Version
                        buffer.WriteByte(payloadOffset, MajorVersion);
                        buffer.WriteByte(payloadOffset + 1, MinorVersion);
                        buffer.WriteUnsignedShortBigEndian(payloadOffset + 2, BuildNumber);
                        buffer.WriteUnsignedShortBigEndian(payloadOffset + 4, BuildNumber);
                        length = 6;
                        break;

                    case 1:
                        // Encryption
                        buffer.WriteByte(payloadOffset, (byte)EncryptionOption);
                        length = 1;
                        break;

                    case 2:
                        // InstanceName
                        if (!string.IsNullOrEmpty(InstanceName))
                        {
                            length = (ushort)InstanceName.Length;
                            for (var j = 0; j < length; j++)
                            {
                                buffer.WriteByte(payloadOffset + j, (byte)InstanceName[j]);
                            }
                        }
                        buffer.WriteByte(payloadOffset + length, 0);
                        length += 1;
                        break;

                    case 3:
                        // ThreadId
                        buffer.WriteUnsignedIntBigEndian(payloadOffset, ThreadId);
                        length = 4;
                        break;

                    case 4:
                        // MARS
                        buffer.WriteByte(payloadOffset, (byte)(Mars ? 1 : 0));
                        length = 1;
                        break;

                    case 5:
                        //ConnectionId_ActivityId_ActivitySequence
                        if (ConnectionId != null)
                        {
                            throw new NotImplementedException();
                        }
                        length = 0;
                        break;

                    case 6:
                        // FedAuthRequired
                        buffer.WriteByte(payloadOffset, 1);
                        length = 1;
                        break;

                    default:
                        throw new InvalidOperationException();
                }

                buffer.WriteUnsignedShortBigEndian(bufferOffset, length);
                bufferOffset += 2;
                payloadOffset += length;
            }

            buffer[bufferOffset] = 0xFF; //Terminator;
        }

        public static PreloginStream ParseResponse(ReadOnlySequence<byte> buffer)
        {
            var span = buffer.FirstSpan;

            var (versionOffset, _) = ParseOption(span, 0);
            if (versionOffset != 7 * SizePerOption + 1)
            {
                throw new InvalidOperationException();
            }
            var majorVersion = span[versionOffset];
            var minorVersion = span[versionOffset + 1];
            var buildNumber = span.ReadUshortBigEndian(versionOffset + 2);
            var subBuildNumber = span.ReadUshortBigEndian(versionOffset + 4);

            var (encryptionOffset, _) = ParseOption(span, 1);
            var encryption = (EncryptionOption)span[encryptionOffset];

            ParseOption(span, 2);
            ParseOption(span, 3);
            ParseOption(span, 4);
            ParseOption(span, 5);

            var (fedAuthOffset, _) = ParseOption(span, 6);
            var fedAuthRequired = span[fedAuthOffset] == 1;
            if (fedAuthRequired)
            {
                throw new NotImplementedException();
            }

            if (span[7 * SizePerOption] != 0xFF)
            {
                throw new InvalidOperationException();
            }

            return new PreloginStream(majorVersion, minorVersion, buildNumber, subBuildNumber, encryption, null, 0, false, null, null, null);

            static (int Offset, int Length) ParseOption(ReadOnlySpan<byte> span, byte expectedOption)
            {
                var offset = expectedOption * SizePerOption;
                if (span[offset] != expectedOption)
                {
                    throw new InvalidOperationException();
                }

                return (span.ReadUshortBigEndian(offset + 1), span.ReadUshortBigEndian(offset + 3));
            }
        }
    }
}
