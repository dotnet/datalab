using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Woodstar.Tds;

// Used to link up CommandContexts constructed before a session is available with an actual session or statement later on.
interface ICommandExecutionProvider
{
    public CommandExecution Get(in CommandContext context);
}

readonly struct CommandContext
{
    readonly IOCompletionPair _completionPair;
    readonly CommandExecution _commandExecution;
    readonly ICommandExecutionProvider? _provider;

    CommandContext(IOCompletionPair completionPair, ICommandExecutionProvider provider)
    {
        _completionPair = completionPair;
        _provider = provider;
        _commandExecution = default!;
    }

    CommandContext(IOCompletionPair completionPair, CommandExecution commandExecution)
    {
        _completionPair = completionPair;
        _commandExecution = commandExecution;
    }

    // Copy constructor
    CommandContext(IOCompletionPair completionPair, CommandExecution commandExecution, ICommandExecutionProvider? provider)
    {
        _completionPair = completionPair;
        _commandExecution = commandExecution;
        _provider = provider;
    }

    /// Only reliable to be called once, store the result if multiple lookups are needed.
    public CommandExecution GetCommandExecution()
    {
        if (_provider is { } provider)
            return provider.Get(this);

        return _commandExecution;
    }

    public bool IsCompleted => _completionPair.ReadSlot.IsCompleted;
    public ValueTask<WriteResult> WriteTask => _completionPair.Write;
    public OperationSlot ReadSlot => _completionPair.ReadSlot;

    public ValueTask<Operation> GetOperation() => _completionPair.SelectAsync();

    public CommandContext WithIOCompletionPair(IOCompletionPair completionPair)
        => new(completionPair, _commandExecution, _provider);

    public static CommandContext Create(IOCompletionPair completionPair, ICommandExecutionProvider provider)
        => new(completionPair, provider);

    public static CommandContext Create(IOCompletionPair completionPair, CommandExecution commandExecution)
        => new(completionPair, commandExecution);
}

readonly struct CommandContextBatch : IEnumerable<CommandContext>
{
    readonly CommandContext _context;
    readonly CommandContext[]? _contexts;

    CommandContextBatch(CommandContext[] contexts)
    {
        if (contexts.Length == 0)
            // Throw inlined as constructors will never be inlined.
            throw new ArgumentException("Array cannot be empty.", nameof(contexts));

        _contexts = contexts;
    }

    CommandContextBatch(CommandContext context)
        => _context = context;

    public static CommandContextBatch Create(params CommandContext[] contexts)
        => new(contexts);

    public static CommandContextBatch Create(CommandContext context)
    {
#if !NETSTANDARD2_0
        return new(context);
#else
        // MemoryMarshal.CreateReadOnlySpan cannot be implemented safely (could make two codepaths for the enumerator, but it's ns2.0 so who cares).
        return new(new[] { context });
#endif
    }

    public static implicit operator CommandContextBatch(CommandContext commandContext) => Create(commandContext);

    public CommandContext Single => Length is 1 ? _context : throw new InvalidOperationException();
    
    public int Length => _contexts?.Length ?? 1;

    public bool AllCompleted
    {
        get
        {
#if !NETSTANDARD2_0
            var contexts = _contexts ?? new ReadOnlySpan<CommandContext>(_context);
#else
            var contexts = _contexts!;
#endif

            foreach (var command in contexts)
            {
                var op = command.GetOperation();
                if (!op.IsCompleted || !op.Result.IsCompleted)
                    return false;
            }

            return true;
        }
    }

    public struct Enumerator: IEnumerator<CommandContext>
    {
        readonly CommandContext[]? _contexts;
        CommandContext _current;
        int _index;

        internal Enumerator(CommandContextBatch instance)
        {
            if (instance._contexts is null)
            {
                _current = instance._context;
                _index = -2;
            }
            else
            {
                _contexts = instance._contexts;
                _index = 0;
            }
        }

        public bool MoveNext()
        {
            var contexts = _contexts;
            // Single element case.
            if (contexts is null)
            {
                if (_index == -1)
                    return false;

                if (_index != -2)
                    ThrowInvalidEnumerator();

                _index++;
                return true;
            }

            if ((uint)_index < (uint)contexts.Length)
            {
                _current = contexts[_index];
                _index++;
                return true;
            }

            _current = default;
            _index = contexts.Length + 1;
            return false;

            static void ThrowInvalidEnumerator() => throw new InvalidOperationException("Invalid Enumerator, default value?");
        }

        public readonly CommandContext Current => _current;

        public void Reset()
        {
            if (_contexts is null)
            {
                _index = -2;
            }
            else
            {
                _index = 0;
                _current = default;
            }
        }

        readonly object IEnumerator.Current => Current;
        public void Dispose() { }
    }

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<CommandContext> IEnumerable<CommandContext>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
