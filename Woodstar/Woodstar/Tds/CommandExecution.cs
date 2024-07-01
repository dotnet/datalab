using System;
using System.Collections.Generic;

namespace Woodstar.Tds;

interface ICommandSession
{
    public Statement? Statement { get; }

    public IReadOnlyCollection<IParameterSession>? WritableParameters { get; }
    public void CloseWritableParameters();

    /// <summary>
    /// Invoked after the statement has been successfully prepared, providing backend information about the statement.
    /// </summary>
    /// <param name="statement"></param>
    public void CompletePreparation(Statement statement);
    public void CancelPreparation(Exception? ex);
}

readonly struct CommandExecution
{
    readonly ExecutionFlags _executionFlags;
    readonly SqlCommandFlags _flags;
    readonly object _sessionOrStatement;

    CommandExecution(ExecutionFlags executionFlags, SqlCommandFlags flags, object? sessionOrStatement)
    {
        _executionFlags = executionFlags;
        _flags = flags;
        // string.Empty is a sentinel so we always know whether we're looking at a default struct (which would have null).
        _sessionOrStatement = sessionOrStatement ?? string.Empty;
    }

    // TODO improve the api of this 'thing'.
    public (ExecutionFlags ExecutionFlags, SqlCommandFlags Flags) TryGetSessionOrStatement(out ICommandSession? session, out Statement? statement)
    {
        var sessionOrStatement = _sessionOrStatement;
        if (sessionOrStatement is null)
            ThrowDefaultValue();

        if (sessionOrStatement is Statement statementValue)
        {
            statement = statementValue;
            session = null;
        }
        else if (sessionOrStatement is ICommandSession sessionValue)
        {
            statement = null;
            session = sessionValue;
        }
        else
        {
            // The sentinel case.
            statement = null;
            session = null;
        }

        return (_executionFlags, _flags);
    }

    static void ThrowDefaultValue() => throw new InvalidOperationException($"This operation cannot be performed on a default value of {nameof(CommandExecution)}.");

    public static CommandExecution Create(ExecutionFlags executionFlags, SqlCommandFlags flags) => new(executionFlags, flags, null);
    public static CommandExecution Create(ExecutionFlags executionFlags, SqlCommandFlags flags, ICommandSession session)
    {
        var prepared = executionFlags.HasPrepared();
        if (!executionFlags.HasPreparing() && !prepared)
            throw new ArgumentException("Execution flags does not have Preparing or Prepared.", nameof(executionFlags));

        // We cannot check the inverse for Preparing because a connection can be preparing a previously completed statement.
        if (prepared && session.Statement?.IsComplete == false)
            throw new ArgumentException("Execution flags has Prepared but session.Statement is not complete.", nameof(executionFlags));

        return new(executionFlags, flags, session);
    }
    public static CommandExecution Create(ExecutionFlags executionFlags, SqlCommandFlags flags, Statement statement)
    {
        if (!executionFlags.HasPrepared())
            throw new ArgumentException("Execution flags does not have Prepared.", nameof(executionFlags));

        if (!statement.IsComplete)
            throw new ArgumentException("Statement is not complete.", nameof(statement));

        return new(executionFlags, flags, statement);
    }
}
