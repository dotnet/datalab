using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Woodstar.Tds;

interface IConnectionFactory<T> where T : Protocol
{
    T Create(TimeSpan timeout);
    ValueTask<T> CreateAsync(CancellationToken cancellationToken);
}

class ConnectionSource<T>: IDisposable where T : Protocol
{
    readonly object?[] _connections;
    readonly IConnectionFactory<T> _factory;
    CancellationTokenSource? _connectionTimeoutSource;
    volatile bool _disposed;

    public ConnectionSource(IConnectionFactory<T> factory, int maxPoolSize)
    {
        if (maxPoolSize <= 0)
            // Throw inlined as constructors will never be inlined.
            throw new ArgumentOutOfRangeException(nameof(maxPoolSize), "Cannot be zero or negative.");
        _connections = new object[maxPoolSize];
        _factory = factory;
    }

    object TakenSentinel => this;

    void ThrowIfDisposed()
    {
        if (_disposed)
            ThrowObjectDisposed();

        static void ThrowObjectDisposed() => throw new ObjectDisposedException(nameof(ConnectionSource<T>));
    }

    bool IsConnection([NotNullWhen(true)] object? item, [MaybeNullWhen(false)]out T instance)
    {
        if (item is not null && !ReferenceEquals(TakenSentinel, item))
        {
            instance = Unsafe.As<object, T>(ref item);
            return true;
        }

        instance = default;
        return false;
    }

    // Instead of ad-hoc we could also do this as an array sort on a timer as it only has to be an approximate best match.
    /// <summary>
    /// Either returns a non-null opSlot, or an index to create a new connection on.
    /// </summary>
    /// <param name="exclusiveUse"></param>
    /// <param name="allowPipelining"></param>
    /// <param name="slot"></param>
    /// <param name="slotIndex"></param>
    /// <param name="connectionSlot"></param>
    /// <param name="cancellationToken"></param>
    /// <returns>true if index or slot found, false if we couldn't pipeline onto any, otherwise always true.</returns>
    bool TryGetSlot(bool exclusiveUse, bool allowPipelining, OperationSlot? slot, out int slotIndex, out OperationSlot? connectionSlot, CancellationToken cancellationToken)
    {
        var connections = _connections;
        int candidateIndex;
        connectionSlot = null;
        do
        {
            (bool PendingExclusiveUse, int Pending) candidateKey = (true, int.MaxValue);
            candidateIndex = -1;
            T? candidateConn = null;
            OperationSlot? connSlot;
            for (var i = 0; i < connections.Length; i++)
            {
                var item = connections[i];
                if (IsConnection(item, out var conn))
                {
                    var state = conn.State;
                    // We should not do anything when we run into a draining connection.
                    if (state is ProtocolState.Draining)
                        continue;

                    if (state is ProtocolState.Completed && ReferenceEquals(Interlocked.CompareExchange(ref _connections[i], TakenSentinel, item), item))
                    {
                        // This is a completed connection which we can replace with a new one.
                        slotIndex = i;
                        connectionSlot = null;
                        return true;
                    }

                    // If it's idle then that's the best scenario, return immediately.
                    if (slot is not null)
                    {
                        if (conn.TryStartOperation(slot, OperationBehavior.ImmediateOnly | (exclusiveUse ? OperationBehavior.ExclusiveUse : OperationBehavior.None), cancellationToken))
                        {
                            slotIndex = i;
                            connectionSlot = slot;
                            return true;
                        }
                    }
                    else if (conn.TryStartOperation(out connSlot, OperationBehavior.ImmediateOnly | (exclusiveUse ? OperationBehavior.ExclusiveUse : OperationBehavior.None), cancellationToken))
                    {
                        slotIndex = i;
                        connectionSlot = connSlot;
                        return true;
                    }

                    var currentKey = (conn.PendingExclusiveUse, conn.Pending);
                    if (candidateConn is null || (!currentKey.PendingExclusiveUse && candidateKey.PendingExclusiveUse) || currentKey.Pending < candidateKey.Pending)
                    {
                        candidateKey = currentKey;
                        candidateIndex = i;
                        candidateConn = conn;
                    }
                }
                else if (!ReferenceEquals(TakenSentinel, item) && ReferenceEquals(Interlocked.CompareExchange(ref _connections[i], TakenSentinel, item), item))
                {
                    // This is an empty slot which we can fill.
                    slotIndex = i;
                    connectionSlot = null;
                    return true;
                }
            }

            // Note: not 'ImmediateOnly' in the candidate flow as we're apparently exhausted.
            // If we want full fairness we can put a channel in between all this.
            // (an earlier caller can get stuck behind slow ops, getting overtaken by 'luckier' callers).
            if (allowPipelining && candidateConn is not null)
            {
                if (slot is not null)
                {
                    if (candidateConn.TryStartOperation(slot, exclusiveUse ? OperationBehavior.ExclusiveUse : OperationBehavior.None, cancellationToken))
                    {
                        connectionSlot = slot;
                        slotIndex = candidateIndex;
                    }
                }
                else if (candidateConn.TryStartOperation(out connSlot, exclusiveUse ? OperationBehavior.ExclusiveUse : OperationBehavior.None, cancellationToken))
                {
                    connectionSlot = connSlot;
                    slotIndex = candidateIndex;
                }
            }
            else if (!allowPipelining)
            {
                // Can only get here if it's all full (as long as all connections hold the same limits, otherwise we might miss cases).
                slotIndex = default;
                return false;
            }

        } while (allowPipelining && connectionSlot is null);

        slotIndex = candidateIndex;
        return true;
    }

#if !NETSTANDARD2_0
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    // Throws inlined as it will not be inlined and it's not commonly called.
    async ValueTask<OperationSlot> OpenConnection(int index, bool exclusiveUse, bool async, OperationSlot? slot = null, TimeSpan timeout = default, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = Volatile.Read(ref _connections[index]);
        Debug.Assert(ReferenceEquals(item, TakenSentinel));
        T? conn;
        CancellationTokenSource? timeoutSource = null;
        CancellationTokenRegistration registration = default;
        if (async && timeout != default && timeout != Timeout.InfiniteTimeSpan)
        {
            timeoutSource = Interlocked.Exchange(ref _connectionTimeoutSource, null) ?? new CancellationTokenSource();
            timeoutSource.CancelAfter(timeout);
            if (cancellationToken.CanBeCanceled)
                registration = cancellationToken.UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(), timeoutSource);

            cancellationToken = timeoutSource.Token;
        }
        try
        {
            conn = await (async ? _factory.CreateAsync(cancellationToken) : new ValueTask<T>(_factory.Create(timeout))).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeoutSource?.Token.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The operation has timed out.", ex);
        }
        catch
        {
            // Remove the sentinel.
            Volatile.Write(ref _connections[index], null);
            throw;
        }
        finally
        {
            await registration.DisposeAsync();
            if (timeoutSource is not null && _connectionTimeoutSource is null && timeoutSource.TryReset())
                Interlocked.Exchange(ref _connectionTimeoutSource, timeoutSource);
        }

        ThrowIfDisposed();
        OperationSlot? connectionSlot;
        if (slot is not null)
        {
            if (!conn.TryStartOperation(slot, OperationBehavior.ImmediateOnly | (exclusiveUse ? OperationBehavior.ExclusiveUse : OperationBehavior.None), CancellationToken.None))
                throw new InvalidOperationException("Could not start an operation on a fresh connection.");

            connectionSlot = slot;
        }
        else if (!conn.TryStartOperation(out connectionSlot, OperationBehavior.ImmediateOnly | (exclusiveUse ? OperationBehavior.ExclusiveUse : OperationBehavior.None), CancellationToken.None))
            throw new InvalidOperationException("Could not start an operation on a fresh connection.");

        // Make sure this won't be reordered to make the instance visible to other threads before we get a spot.
        Volatile.Write(ref _connections[index], conn);
        return connectionSlot;
    }

    public OperationSlot Get(bool exclusiveUse, TimeSpan connectionTimeout)
    {
        ThrowIfDisposed();
        if (!TryGetSlot(exclusiveUse, allowPipelining: false, slot: null, out var index, out var connectionSlot, CancellationToken.None))
            ThrowSourceExhausted();

        connectionSlot ??= OpenConnection(index, exclusiveUse, async: false, slot: null, connectionTimeout).GetAwaiter().GetResult();
        Debug.Assert(connectionSlot is not null);
        Debug.Assert(connectionSlot.Task.IsCompletedSuccessfully);
        return connectionSlot;
    }

    public ValueTask<OperationSlot> GetAsync(bool exclusiveUse, TimeSpan connectionTimeout, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!TryGetSlot(exclusiveUse, allowPipelining: true, slot: null, out var index, out var connectionSlot, cancellationToken))
            ThrowSourceExhausted();

        return connectionSlot is not null ? new(connectionSlot) : OpenConnection(index, exclusiveUse, async: true, slot: null, connectionTimeout, cancellationToken);
    }

    // No exclusive use here, it doesn't make much sense to have it atm.
    public ValueTask BindAsync(OperationSlot slot, TimeSpan connectionTimeout, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!TryGetSlot(exclusiveUse: false, allowPipelining: true, slot, out var index, out var connectionSlot, CancellationToken.None))
            ThrowSourceExhausted();

        return connectionSlot is not null ? new() : OpenConnectionIgnoreResult(this, index, exclusiveUse: false, async: true, slot, connectionTimeout, cancellationToken);

#if !NETSTANDARD2_0
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
#endif
        static async ValueTask OpenConnectionIgnoreResult(ConnectionSource<T> instance, int slotIndex, bool exclusiveUse, bool async, OperationSlot? slot, TimeSpan connectionTimeout, CancellationToken cancellationToken)
        {
            await instance.OpenConnection(slotIndex, exclusiveUse, async, slot, connectionTimeout, cancellationToken).ConfigureAwait(false);
        }
    }

    static void ThrowSourceExhausted() => throw new InvalidOperationException("ConnectionSource is exhausted, there are no empty slots or connections idle enough to take new work.");

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (var connection in _connections)
        {
            if (IsConnection(connection, out var conn))
            {
                // We're just letting it run, draining can take a while and we're not going to wait.
                var _ = Task.Run(async () =>
                {
                    try
                    {
                        await conn.CompleteAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // TODO This 'should' log something.
                    }
                });
            }
        }
    }
}
