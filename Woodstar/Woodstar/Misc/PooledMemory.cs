using System;
using System.Buffers;

namespace Woodstar;

readonly struct PooledMemory<T>: IDisposable
{
    readonly object _collection;
    readonly int _length;
    readonly bool _pooledArray;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="array"></param>
    /// <param name="length"></param>
    /// <param name="pooledArray">Whether the array originates from ArrayPool{CommandParameter}.Shared or is a normal alloc.</param>
    public PooledMemory(T[] array, int length, bool pooledArray = true)
    {
        _collection = array ?? throw new ArgumentNullException(nameof(array));
        _length = length;
        _pooledArray = pooledArray;
    }

    public PooledMemory(IMemoryOwner<T> memoryOwner)
        => _collection = memoryOwner ?? throw new ArgumentNullException(nameof(memoryOwner));

    public int Length
    {
        get
        {
            ThrowIfDefault();
            return _length;
        }
    }
    public bool IsEmpty => Length == 0;
    public bool IsDefault => _collection is null;

    public ReadOnlySpan<T> Span
    {
        get
        {
            ThrowIfDefault();
            if (_collection is T[] collection)
                return collection.AsSpan(0, _length);

            return ((IMemoryOwner<T>)_collection).Memory.Span;
        }
    }

    public ReadOnlyMemory<T> Memory
    {
        get
        {
            ThrowIfDefault();
            if (_collection is T[] collection)
                return new ReadOnlyMemory<T>(collection, 0, _length);

            return ((IMemoryOwner<T>)_collection).Memory;
        }
    }

    void ThrowIfDefault()
    {
        if (IsDefault)
            throw new InvalidOperationException($"This operation cannot be performed on a default value of {nameof(PooledMemory<T>)}.");
    }

    public void Dispose()
    {
        if (IsDefault)
            return;

        if (_collection is T[] collection)
        {
            if (_pooledArray)
                ArrayPool<T>.Shared.Return(collection, clearArray: true); // Make sure to clear references for GC.
        }
        else
            ((IMemoryOwner<T>)_collection).Dispose();
    }

    public static PooledMemory<T> Empty => new(Array.Empty<T>(), 0, pooledArray: false);
}
