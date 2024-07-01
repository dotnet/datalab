// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Threading;

namespace Woodstar.Pipelines;

class IOQueue : PipeScheduler
#if !NETSTANDARD2_0
    , IThreadPoolWorkItem
#endif
{
    readonly ConcurrentQueue<Work> _workItems = new();
    int _doingWork;

    public override void Schedule(Action<object?> action, object? state)
    {
        _workItems.Enqueue(new Work(action, state));

        // Set working if it wasn't (via atomic Interlocked).
        if (Interlocked.CompareExchange(ref _doingWork, 1, 0) == 0)
        {
            // Wasn't working, schedule.
#if NETSTANDARD2_0
            System.Threading.ThreadPool.UnsafeQueueUserWorkItem(o => WaitCallback(o), this);
#else
            System.Threading.ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
#endif
        }
    }

    static void WaitCallback(object state)
    {
        ((IOQueue)state).ExecuteCore();
    }

#if !NETSTANDARD2_0
    void IThreadPoolWorkItem.Execute() => ExecuteCore();
#endif

    void ExecuteCore()
    {
        while (true)
        {
            while (_workItems.TryDequeue(out Work item))
            {
                item.Callback(item.State);
            }

            // All work done.

            // Set _doingWork (0 == false) prior to checking IsEmpty to catch any missed work in interim.
            // This doesn't need to be volatile due to the following barrier (i.e. it is volatile).
            _doingWork = 0;

            // Ensure _doingWork is written before IsEmpty is read.
            // As they are two different memory locations, we insert a barrier to guarantee ordering.
            Thread.MemoryBarrier();

            // Check if there is work to do
            if (_workItems.IsEmpty)
            {
                // Nothing to do, exit.
                break;
            }

            // Is work, can we set it as active again (via atomic Interlocked), prior to scheduling?
            if (Interlocked.Exchange(ref _doingWork, 1) == 1)
            {
                // Execute has been rescheduled already, exit.
                break;
            }

            // Is work, wasn't already scheduled so continue loop.
        }
    }

    readonly struct Work
    {
        public readonly Action<object?> Callback;
        public readonly object? State;

        public Work(Action<object?> callback, object? state)
        {
            Callback = callback;
            State = state;
        }
    }
}
