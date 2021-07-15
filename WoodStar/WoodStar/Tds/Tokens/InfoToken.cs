using System;
using System.Buffers;

namespace WoodStar;

public sealed class InfoToken : IToken
{
    public TokenType TokenType => TokenType.INFO;
    public InfoToken(ushort length, int number, byte state, byte @class, string msgText, string serverName, string procName, int lineNumber)
    {
        Length = length;
        Number = number;
        State = state;
        Class = @class;
        MsgText = msgText;
        ServerName = serverName;
        ProcName = procName;
        LineNumber = lineNumber;
    }

    public int TokenLength => Length + 1 + 2;

    public ushort Length { get; }
    public int Number { get; }
    public byte State { get; }
    public byte Class { get; }
    public string MsgText { get; }
    public string ServerName { get; }
    public string ProcName { get; }
    public int LineNumber { get; }

    public static InfoToken Parse(ref SequenceReader<byte> reader)
    {
        if (reader.TryReadLittleEndian(out ushort length)
            && reader.TryReadLittleEndian(out int number)
            && reader.TryRead(out var state)
            && reader.TryRead(out var @class)
            && reader.ReadUsVarchar(out var msgText)
            && reader.ReadBVarchar(out var serverName)
            && reader.ReadBVarchar(out var procName)
            && reader.TryReadLittleEndian(out int lineNumber))
        {
            return new InfoToken(length, number, state, @class, msgText, serverName, procName, lineNumber);
        }

        throw new ParsingException();
    }
}
