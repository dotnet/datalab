namespace Woodstar.Tds.Tokens;

class ReturnStatusToken : Token
{
    public ReturnStatusToken(int value)
        => Value = value;

    public int Value { get; }
}
