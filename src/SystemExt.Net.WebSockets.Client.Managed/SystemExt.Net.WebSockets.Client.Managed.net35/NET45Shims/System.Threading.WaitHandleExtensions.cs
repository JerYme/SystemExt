using System.Threading;
using System.Threading.Tasks;

namespace System.Threading
{
    public static class WaitHandleExtensions
    {
        public static Task WaitAsync(this WaitHandle waitHandle)
        {
            if (waitHandle.WaitOne(0, false)) return TaskEx.TaskCompleted;
            var tcs = new TaskCompletionSource<bool>();
            ThreadPool.RegisterWaitForSingleObject(waitHandle, (state, timedOut) => tcs.TrySetResult(true), null, Timeout.Infinite, true);
            return tcs.Task;
        }
        public static Task WaitAsync(this SemaphoreSlim semaphore)
        {
            if (semaphore.Wait(0)) return TaskEx.TaskCompleted;
            var tcs = new TaskCompletionSource<bool>();
            WaitOrTimerCallback waitOrTimerCallback = null;
            waitOrTimerCallback = (state, timedOut) =>
            {
                if (semaphore.Wait(0))
                {
                    tcs.TrySetResult(true);
                    return;
                }
                ThreadPool.RegisterWaitForSingleObject(semaphore.AvailableWaitHandle, waitOrTimerCallback, null, Timeout.Infinite, true);
            };
            waitOrTimerCallback(null, false);
            return tcs.Task;
        }
    }
}