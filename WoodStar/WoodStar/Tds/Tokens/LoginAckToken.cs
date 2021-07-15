using System;
using System.Buffers;

namespace WoodStar;

public sealed class LoginAckToken : IToken
{
    public TokenType TokenType => TokenType.LOGINACK;

    public LoginAckToken(ushort length, byte @interface, byte[] tdsVersion, string programName, Version programVersion)
    {
        Length = length;
        Interface = @interface;
        TdsVersion = tdsVersion;
        ProgramName = programName;
        ProgramVersion = programVersion;
    }

    public int TokenLength => Length + 1 + 2;
    public ushort Length { get; }
    public byte Interface { get; }
    public byte[] TdsVersion { get; }
    public string ProgramName { get; }
    public Version ProgramVersion { get; }

    public static LoginAckToken Parse(ref SequenceReader<byte> reader)
    {
        if (reader.TryReadLittleEndian(out ushort length)
            && reader.TryRead(out var @interface)
            && reader.TryReadByteArray(size: 4, out var tdsVersion)
            && reader.ReadBVarchar(out var programName)
            && reader.TryReadByteArray(size: 4, out var versionBytes))
        {
            var version = new Version(versionBytes[0], versionBytes[1], (versionBytes[2] << 8) | versionBytes[3]);

            return new LoginAckToken(length, @interface, tdsVersion, programName, version);
        }

        throw new ParsingException();
    }
}
