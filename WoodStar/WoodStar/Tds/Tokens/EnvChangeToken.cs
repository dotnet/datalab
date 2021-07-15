using System;
using System.Buffers;

namespace WoodStar.Tds.Tokens;

public sealed class EnvChangeToken : IToken
{
    public TokenType TokenType => TokenType.ENVCHANGE;
    public EnvChangeToken(ushort length, byte type, object newValue, object oldValue)
    {
        Length = length;
        Type = type;
        NewValue = newValue;
        OldValue = oldValue;
    }

    public int TokenLength => Length + 1 + 2;

    public ushort Length { get; }
    public byte Type { get; }
    public object NewValue { get; }
    public object OldValue { get; }

    public static EnvChangeToken Parse(ref SequenceReader<byte> reader)
    {
        if (reader.TryReadLittleEndian(out ushort length)
            && reader.TryRead(out var type))
        {
            if ((type == 2 || type == 4)
                && reader.ReadBVarchar(out var newValue)
                && reader.ReadBVarchar(out var oldValue))
            {
                return new EnvChangeToken(length, type, newValue, oldValue);
            }

            if (type == 18
                && reader.TryRead(out _)
                && reader.TryRead(out _))
            {
                return new EnvChangeToken(length, type, 0, 0);
            }
        }

        throw new ParsingException();
    }
}
