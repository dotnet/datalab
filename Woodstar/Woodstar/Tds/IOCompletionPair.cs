using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Woodstar.Tds;

readonly struct WriteResult
{
    const long UnknownBytesWritten = long.MinValue;
    public WriteResult(long bytesWritten) => BytesWritten = bytesWritten;

    public long BytesWritten { get; }
    public static WriteResult Unknown => new(UnknownBytesWritten);
}

readonly struct IOCompletionPair
{
    public IOCompletionPair(ValueTask<WriteResult> write, OperationSlot readSlot)
    {
        // Usually this task is done already so we want to keep it as a ValueTask,
        // however we might need to attach multiple continuations if it's still running.
        Write = write.Preserve();
        ReadSlot = readSlot;
    }

    public ValueTask<WriteResult> Write { get; }
    ValueTask<Operation> Read => ReadSlot.Task;
    public OperationSlot ReadSlot { get; }

    /// <summary>
    /// Checks whether Write or Read is completed (in that order) before waiting on either for one to complete until Read or both are.
    /// If Read is completed we don't wait for Write anymore but we will check its status on future invocations.
    /// </summary>
    /// <returns></returns>
    public ValueTask<Operation> SelectAsync()
    {
        // Internal note, all exceptions should only exist wrapped in a task.

        // Return read when it is completed but only when write is completed successfully or still running.
        if (Write.IsCompletedSuccessfully || (!Write.IsCompleted && Read.IsCompleted))
            return Read;

        if (Write.IsFaulted || Write.IsCanceled)
        {
            try
            {
                Write.GetAwaiter().GetResult();
            }
            catch(Exception ex)
            {
                return new ValueTask<Operation>(Task.FromException<Operation>(ex));
            }
        }

        // Neither are completed yet.
        return Core(this);

#if !NETSTANDARD2_0
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
        static async ValueTask<Operation> Core(IOCompletionPair instance)
        {
            var readTask = instance.ReadSlot.Task.AsTask();
            await Task.WhenAny(instance.Write.AsTask(), readTask).ConfigureAwait(false);
            if (instance.Write.IsCompletedSuccessfully || (!instance.Write.IsCompleted && instance.Read.IsCompleted))
                return await readTask.ConfigureAwait(false);

            if (instance.Write.IsFaulted || instance.Write.IsCanceled)
            {
                instance.Write.GetAwaiter().GetResult();
                return default;
            }

            ThrowUnreachable();
            return default;

            static void ThrowUnreachable() => throw new UnreachableException();
        }
    }
}
