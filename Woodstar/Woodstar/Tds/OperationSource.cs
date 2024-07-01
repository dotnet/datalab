using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace Woodstar.Tds;

// TODO this *could* be a struct as well, to make the hierarchy smaller, though the struct will take more space everywhere a slot is used. atomicity may also be an issue.
// The friendly 'user facing' part of the hierarchy.
abstract class OperationSlot
{
    /// <summary>
    /// The connection the slot belongs to, can be null once completed or when not yet bound.
    /// </summary>
    public abstract Protocol? Protocol { get; }

    public abstract bool IsCompleted { get; }
    public abstract bool IsCompletedSuccessfully { get; }

    /// <summary>
    /// The task will be activated once the operation can read from the connection.
    /// </summary>
    public abstract ValueTask<Operation> Task { get; }
}

// The actual operation, once a waiter is allowed to run, containing a non-null protocol reference and completion apis.
readonly struct Operation: IDisposable
{
    readonly OperationSource _source;

    internal Operation(OperationSource source, Protocol protocol)
    {
        _source = source;
        Protocol = protocol;
    }

    public Protocol Protocol { get; }

    public bool IsCompleted => _source.IsCompleted;

    // TODO Verify in which cases it is possible that not being able to complete while having been activated would happen.
    public void Complete(Exception? exception = null)
    {
        if(_source?.TryComplete(exception) == false && exception is not null && _source.IsCompletedSuccessfully)
            ThrowInvalidOperation();

        static void ThrowInvalidOperation() => throw new InvalidOperationException("Could not complete the operation with an exception as it was already completed successfully.");
    }
    public void Dispose() => Complete();
}

abstract class OperationSource: OperationSlot
{
    [Flags]
    enum OperationSourceFlags
    {
        Created = 0,
        Pooled = 1,
        Activated = 2,
        Faulted = 4,
        Completed = 8,
        Canceled = 16
    }

    OperationSourceFlags _state;
    CancellationTokenRegistration _cancellationRegistration;
    Protocol? _protocol;

    ManualResetValueTaskSourceCore<Operation> _tcs;
    protected ref ManualResetValueTaskSourceCore<Operation> ValueTaskSource => ref _tcs;

    protected OperationSource(Protocol? protocol, bool pooled = false)
    {
        _protocol = protocol;
        if (pooled)
        {
            if (protocol is null)
                throw new ArgumentNullException(nameof(protocol), "Pooled sources cannot be unbound.");

            _tcs.SetResult(new Operation(this, protocol));
            _state = OperationSourceFlags.Pooled | OperationSourceFlags.Activated;
        }
        else
        {
            _state = OperationSourceFlags.Created;
        }
    }

    [MemberNotNullWhen(true, nameof(_protocol))]
    public sealed override bool IsCompleted => (_state & OperationSourceFlags.Completed) != 0;
    public sealed override Protocol? Protocol => IsCompleted ? null : _protocol;
    [MemberNotNullWhen(true, nameof(_protocol))]
    protected bool IsPooled => (_state & OperationSourceFlags.Pooled) != 0;
    protected bool IsActivated => (_state & OperationSourceFlags.Activated) != 0;
    [MemberNotNullWhen(true, nameof(_cancellationRegistration))]
    protected bool IsCanceled => (_state & OperationSourceFlags.Canceled) != 0;
    public sealed override bool IsCompletedSuccessfully => IsCompleted && (_state & OperationSourceFlags.Faulted) == 0;

#if !NETSTANDARD2_0
    public CancellationToken CancellationToken => _cancellationRegistration.Token;
#else
    public CancellationToken CancellationToken {get; private set;}
#endif

    // Slot can already be completed due to cancellation.
    public bool TryComplete(Exception? exception = null)
    {
        Protocol? protocol;
        if ((protocol = TransitionToCompletion(CancellationToken.None, exception)) is not null)
        {
            CompleteCore(protocol, exception);
            return true;
        }

        return false;
    }

    Protocol? TransitionToCompletion(CancellationToken token, Exception? exception = null)
    {
        var state = (OperationSourceFlags)Volatile.Read(ref Unsafe.As<OperationSourceFlags, int>(ref _state));
        // If we were already completed this is likely another completion from the activated code.
        if ((state & OperationSourceFlags.Completed) != 0)
            return null;

        var newState = (state & ~OperationSourceFlags.Activated) | OperationSourceFlags.Completed;
        if (token.IsCancellationRequested)
            newState |= OperationSourceFlags.Canceled;
        else if (exception is not null)
            newState |= OperationSourceFlags.Faulted;
        if (CompareExchange(ref _state, newState, state) == state)
        {
            // Only change the _tcs if we weren't already activated before.
            if ((state & OperationSourceFlags.Activated) == 0)
            {
                if (exception is not null)
                    _tcs.SetException(exception);
                else if (token.IsCancellationRequested)
                {
                    _tcs.SetException(new OperationCanceledException(token));
                }
            }
            return _protocol;
        }

        return null;
    }

    protected void Reset()
    {
        if (!IsPooled)
            throw new InvalidOperationException("Cannot reuse non-pooled sources.");
        _state = OperationSourceFlags.Pooled | OperationSourceFlags.Activated;
        ResetCore();
    }

    protected abstract void CompleteCore(Protocol protocol, Exception? exception);

    protected virtual void ResetCore() {}
    protected void BindCore(Protocol protocol)
    {
        if (Interlocked.CompareExchange(ref _protocol, protocol, null) != null)
            ThrowAlreadyBound();

        static void ThrowAlreadyBound() => throw new InvalidOperationException("Slot was already bound.");
    }

    protected void AddCancellation(CancellationToken cancellationToken)
    {
        if (IsPooled)
            throw new InvalidOperationException("Cannot cancel pooled sources.");

        if (!cancellationToken.CanBeCanceled)
            return;

        if (_cancellationRegistration != default)
            throw new InvalidOperationException("Cancellation already registered.");

#if NETSTANDARD2_0
        CancellationToken = cancellationToken;
#endif
        _cancellationRegistration = cancellationToken.UnsafeRegister((state, token) =>
        {
            ((OperationSource)state!).TransitionToCompletion(token);
        }, this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void ActivateCore()
    {
        if (_protocol is null)
        {
            ThrowProtocolNull();
            return;
        }
        switch (CompareExchange(ref _state, OperationSourceFlags.Activated, OperationSourceFlags.Created))
        {
            case OperationSourceFlags.Activated:
                ThrowAlreadyActivated();
                break;
            case OperationSourceFlags.Created:
                _cancellationRegistration.Dispose();
                // TODO we probably need an 'Activating' state that makes cancellation no-op.
                // Can be false when we were raced by cancellation completing the source right after we transitioned to the activated state.
                _tcs.SetResult(new Operation(this, _protocol));
                break;
        }

        [MemberNotNull(nameof(_protocol))]
        static void ThrowProtocolNull() => throw new InvalidOperationException("Cannot activate an unbound source.");
        static void ThrowAlreadyActivated() => throw new InvalidOperationException("Already activated.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe T CompareExchange<T>(ref T location, T value, T comparand) where T: unmanaged
        => sizeof(T) switch
        {
            sizeof(int) => (T)(object)Interlocked.CompareExchange(ref Unsafe.As<T, int>(ref location), Unsafe.As<T, int>(ref value), Unsafe.As<T, int>(ref comparand)),
            _ => throw new NotSupportedException("This type cannot be handled atomically")
        };
}
