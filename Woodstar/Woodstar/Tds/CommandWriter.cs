using System.Threading;

namespace Woodstar.Tds;

abstract class CommandWriter
{
    public abstract CommandContext WriteAsync<TCommand>(OperationSlot slot, in TCommand command, bool flushHint = true, CancellationToken cancellationToken = default) where TCommand : ISqlCommand;
}
