using System;
using System.Buffers;

namespace WoodStar.Tds.Tokens;

public sealed class DoneToken : IToken
{
    public TokenType TokenType => TokenType.DONE;

    public int TokenLength => 13;

    public DoneToken(ushort status, ushort currentCommand, ulong doneRowCount)
    {
        Status = status;
        CurrentCommand = currentCommand;
        DoneRowCount = doneRowCount;
    }

    public ushort Status { get; }
    public ushort CurrentCommand { get; }
    public ulong DoneRowCount { get; }

    public static DoneToken Parse(ref SequenceReader<byte> reader)
    {
        if (reader.TryReadLittleEndian(out ushort status)
            && reader.TryReadLittleEndian(out ushort currentCommand)
            && reader.TryReadLittleEndian(out ulong doneRowCount))
        {
            return new DoneToken(status, currentCommand, doneRowCount);
        }

        throw new ParsingException();
    }
}
