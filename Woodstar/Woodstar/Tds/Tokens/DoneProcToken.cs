using System;

namespace Woodstar.Tds.Tokens;

class DoneProcToken : Token
{
    public DoneProcToken(DoneStatus status, ushort currentCommand, ulong doneRowCount)
    {
        Status = status;
        CurrentCommand = currentCommand;
        DoneRowCount = doneRowCount;
    }

    public DoneStatus Status { get; }
    public ushort CurrentCommand { get; }
    public ulong DoneRowCount { get; }

}
