namespace Woodstar.Tds.Tokens;

class EnvChangeToken : Token
{
    public EnvChangeToken(EnvChangeType type, object newValue, object oldValue)
    {
        Type = type;
        NewValue = newValue;
        OldValue = oldValue;
    }

    public EnvChangeType Type { get; }
    public object NewValue { get; }
    public object OldValue { get; }
}


enum EnvChangeType : byte
{
    Language = 2,
    PacketSize = 4,
    ResetAck = 18
}
