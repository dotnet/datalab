using System;

namespace Woodstar.Tds.Tokens;

class DoneToken : Token
{
    public DoneToken(DoneStatus status, ushort currentCommand, ulong doneRowCount)
    {
        Status = status;
        CurrentCommand = currentCommand;
        DoneRowCount = doneRowCount;
    }

    public DoneStatus Status { get; }
    public ushort CurrentCommand { get; }
    public ulong DoneRowCount { get; }

}
