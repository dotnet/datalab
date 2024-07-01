using System;

namespace Woodstar.Tds;

[Flags]
enum ExecutionFlags
{
    Default = 0,

    // Relevant CommandBehavior flags for a single command, these are currently mapped to the same integers, otherwise change extension method CommandBehavior.ToExecutionFlags.
    SchemaOnly = 2,  // column info, no data, no effect on database
    KeyInfo = 4,  // column info + primary key information (if available)
    SingleRow = 8, // data, hint single row and single result, may affect database - doesn't apply to child(chapter) results
    SequentialAccess = 16,

    Unprepared = 128,
    Preparing = 256,
    Prepared = 512,
}

[Flags]
enum SqlCommandFlags
{
    None = 0
}

static class ExecutionFlagsExtensions
{
    public static bool HasUnprepared(this ExecutionFlags flags) => (flags & ExecutionFlags.Unprepared) != 0;
    public static bool HasPreparing(this ExecutionFlags flags) => (flags & ExecutionFlags.Preparing) != 0;
    public static bool HasPrepared(this ExecutionFlags flags) => (flags & ExecutionFlags.Prepared) != 0;
}

interface ISqlCommand
{
    // The underlying values might change so we hand out a copy.
    Values GetValues();

    /// <summary>
    /// Called right before the write commences.
    /// </summary>
    /// <param name="values">Values containing any updated (effective) ExecutionFlags for the protocol it will execute on.</param>
    /// <returns>Execution state that is available for the duration of the execution.</returns>
    CommandExecution BeginExecution(in Values values);

    // Also exposed as a delegate to help struct composition where carrying an interface constraint or boxing is not desired
    // CreateExecution should not depend on instance state anyway, if it needs any then Values.Additional is the vehicle of choice.
    BeginExecutionDelegate BeginExecutionMethod { get; }

    readonly struct Values
    {
        // Can be set to an empty string when a completed statement is also set.
        public required SizedString StatementText { get; init; }
        public required ExecutionFlags ExecutionFlags { get; init; }
        public required TimeSpan ExecutionTimeout { get; init; }
        public Statement? Statement { get; init; }
        public required ParameterContext ParameterContext { get; init; }
        public object? State { get; init; }
        public SqlCommandFlags CommandFlags { get; init; }
    }

    /// <summary>
    /// Called right before the write commences.
    /// </summary>
    /// <param name="values">Values containing any updated (effective) ExecutionFlags for the protocol it will execute on.</param>
    /// <returns>Execution state that is available for the duration of the execution.</returns>
    delegate CommandExecution BeginExecutionDelegate(in Values values);
}
