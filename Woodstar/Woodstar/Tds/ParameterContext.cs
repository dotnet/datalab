using System;

namespace Woodstar.Tds;

[Flags]
enum ParameterContextFlags: short
{
    None = 0,
    AnyUnknownByteCount = 1,
    AnySessions = 8,
    AnyWritableParamSessions = 16,
    AnyWriteState = 32,
}

// Parameters have sensitive disposal semantics so we don't try to handle that in this Dispose.
readonly struct ParameterContext: IDisposable
{
    public PooledMemory<Parameter> Parameters { get; init; }
    public ParameterContextFlags Flags { get; init; }

    public void Dispose()
    {
        Parameters.Dispose();
    }

    public static ParameterContext Empty => new() { Parameters = PooledMemory<Parameter>.Empty };
}

static class ParameterContextExtensions
{
    public static bool HasWriteState(this ParameterContext context)
        => (context.Flags & ParameterContextFlags.AnyWriteState) != 0;

    public static bool HasSessions(this ParameterContext context)
        => (context.Flags & ParameterContextFlags.AnySessions) != 0;

    public static bool HasWritableParamSessions(this ParameterContext context)
        => (context.Flags & ParameterContextFlags.AnyWritableParamSessions) != 0;

    public static bool HasUnknownByteCounts(this ParameterContext context)
        => (context.Flags & ParameterContextFlags.AnyUnknownByteCount) != 0;
}
