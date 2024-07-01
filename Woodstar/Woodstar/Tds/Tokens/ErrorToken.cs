namespace Woodstar.Tds.Tokens;

class ErrorToken : Token
{
    public ErrorToken(int number, byte state, byte @class, string msgText, string serverName, string procName, int lineNumber)
    {
        Number = number;
        State = state;
        Class = @class;
        MsgText = msgText;
        ServerName = serverName;
        ProcName = procName;
        LineNumber = lineNumber;
    }

    public int Number { get; }
    public byte State { get; }
    public byte Class { get; }
    public string MsgText { get; }
    public string ServerName { get; }
    public string ProcName { get; }
    public int LineNumber { get; }
}
