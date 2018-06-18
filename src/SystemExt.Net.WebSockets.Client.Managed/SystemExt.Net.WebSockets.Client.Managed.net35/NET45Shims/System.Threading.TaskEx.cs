using System.Diagnostics;
using System.Threading.Tasks;

namespace System.Threading
{
    public static class TaskEx
    {
        public static Task TaskCompleted;
        public static Task<Flow> TaskContinue;
        public static Task<Flow> TaskBreak;


        static TaskEx()
        {
            TaskCompleted = FromResult(0);
            TaskContinue = FromResult(Flow.Continue());
            TaskBreak = FromResult(Flow.Return());
        }

        public static Task ContinueWithTask(this Task t, Func<Task, Task> continuationFunction)
            => t.ContinueWith(continuationFunction).Unwrap();

        public static Task ContinueWithTask(this Task t, Func<Task, Task> continuationFunction, CancellationToken cancellationToken)
            => t.ContinueWith(continuationFunction, cancellationToken).Unwrap();

        public static Task ContinueWithTask(this Task t, Func<Task, Task> continuationFunction, TaskContinuationOptions options)
            => t.ContinueWith(continuationFunction, options).Unwrap();


        public static Task<TResult> ContinueWithTask<TResult>(this Task t, Func<Task, Task<TResult>> continuationFunction)
            => t.ContinueWith(continuationFunction).Unwrap();

        public static Task<TResult> ContinueWithTask<TResult>(this Task t, Func<Task, Task<TResult>> continuationFunction, CancellationToken cancellationToken)
            => t.ContinueWith(continuationFunction, cancellationToken).Unwrap();


        public static Task ContinueWithTask<TInput>(this Task<TInput> t, Func<Task<TInput>, Task> continuationFunction)
            => t.ContinueWith(continuationFunction).Unwrap();

        public static Task ContinueWithTask<TInput>(this Task<TInput> t, Func<Task<TInput>, Task> continuationFunction, CancellationToken cancellationToken)
            => t.ContinueWith(continuationFunction, cancellationToken).Unwrap();

        public static Task ContinueWithTask<TInput>(this Task<TInput> t, Func<Task<TInput>, Task> continuationFunction, TaskContinuationOptions options)
            => t.ContinueWith(continuationFunction, options).Unwrap();


        public static Task<TResult> ContinueWithTask<TInput, TResult>(this Task<TInput> t, Func<Task<TInput>, Task<TResult>> continuationFunction)
            => t.ContinueWith(continuationFunction).Unwrap();

        public static Task<TResult> ContinueWithTask<TInput, TResult>(this Task<TInput> t, Func<Task<TInput>, Task<TResult>> continuationFunction, CancellationToken cancellationToken)
            => t.ContinueWith(continuationFunction, cancellationToken).Unwrap();

        public static Task<TResult> ContinueWithTask<TInput, TResult>(this Task<TInput> t, Func<Task<TInput>, Task<TResult>> continuationFunction, TaskContinuationOptions options)
            => t.ContinueWith(continuationFunction, options).Unwrap();


        public static Task<T> FromResult<T>(T v)
        {
            var tc = new TaskCompletionSource<T>();
            tc.SetResult(v);
            return tc.Task;
        }

        public static Task<Flow<T>> FromFlow<T>(T v)
        {
            var tc = new TaskCompletionSource<Flow<T>>();
            tc.SetResult(Flow<T>.Return(v));
            return tc.Task;
        }


        public static Task UsingWith(this Task task, params IDisposable[] disposables) => task.FinallyWith(() => Array.ForEach(disposables ?? new IDisposable[0], d => d.Dispose()));
        public static Task<T> UsingWith<T>(this Task<T> task, params IDisposable[] disposables) => task.FinallyWith(() => Array.ForEach(disposables ?? new IDisposable[0], d => d.Dispose()));


        public static Task TryWith(this Task task, Action @try) => TaskCompleted.ContinueWith(t => @try(), TaskContinuationOptions.AttachedToParent).ContinueWith(t => task, TaskContinuationOptions.AttachedToParent).Unwrap();
        public static Task<T> TryWith<T>(this Task<T> task, Action @try) => TaskCompleted.ContinueWith(t => @try(), TaskContinuationOptions.AttachedToParent).ContinueWith(t => task, TaskContinuationOptions.AttachedToParent).Unwrap();

        public static Task FinallyWith(this Task task, Action @finally)
            => task.ContinueWith(t =>
            {
                try
                {
                    Debug.Assert(t.IsCanceled || t.IsCompleted || t.IsFaulted);
                }
                finally
                {
                    @finally();
                }
            }, TaskContinuationOptions.AttachedToParent);

        public static Task<T> FinallyWith<T>(this Task<T> task, Action @finally)
            => task.ContinueWith(t =>
            {
                try
                {
                    return t.Result;
                }
                finally
                {
                    @finally();
                }
            }, TaskContinuationOptions.AttachedToParent);

        public static Task CatchWith<TE>(this Task task, Action<TE> @catch) where TE : Exception
            => task.ContinueWith(t =>
            {
                if (!t.IsFaulted) return;
                var flatten = t.Exception.Flatten();
                flatten.Handle(ex =>
                {
                    var te = ex as TE;
                    if (te != null)
                    {
                        @catch(te);
                        return true;
                    }
                    return false;
                });
            }, TaskContinuationOptions.AttachedToParent);

        public static Task<T> CatchWith<T, TE>(this Task<T> task, Action<TE> @catch) where TE : Exception
            => task.ContinueWith(t =>
            {
                if (!t.IsFaulted) return t.Result;
                var flatten = t.Exception.Flatten();
                flatten.Handle(ex =>
                {
                    var te = ex as TE;
                    if (te != null)
                    {
                        @catch(te);
                        return true;
                    }
                    return false;
                });
                return t.Result;
            }, TaskContinuationOptions.AttachedToParent);

       
        public static Task AsyncLoopTask(Func<Task<Flow>> whileTask)
        {
            var tcs = new TaskCompletionSource<int>();
            var iteration = DoIteration(tcs, whileTask);
            return iteration.ContinueWith(t => tcs.Task, TaskScheduler.Current).Unwrap();
        }

        public static Task<T> AsyncLoopTask<T>(Func<Task<Flow<T>>> whileTask)
        {
            var tcs = new TaskCompletionSource<T>();
            var iteration = DoIteration(tcs, whileTask);
            return iteration.ContinueWith(t => tcs.Task, TaskScheduler.Current).Unwrap();
        }
        
        private static Task DoIteration(TaskCompletionSource<int> tcs, Func<Task<Flow>> whileTask)
        {
            var newTask = whileTask();
            return newTask.ContinueWith(t =>
            {
                if (t.Exception != null)
                {
                    tcs.TrySetException(t.Exception.InnerException);
                }
                else if (!t.Result)
                {
                    tcs.TrySetResult(0);
                }
                else
                {
                    DoIteration(tcs, whileTask);
                }
            }, TaskScheduler.Current);
        }

        private static Task DoIteration<T>(TaskCompletionSource<T> tcs, Func<Task<Flow<T>>> whileTask)
        {
            var newTask = whileTask();
            return newTask.ContinueWith(t =>
            {
                if (t.Exception != null)
                {
                    tcs.TrySetException(t.Exception.InnerException);
                }
                else if (!t.Result)
                {
                    tcs.TrySetResult(t.Result.Value);
                }
                else
                {
                    DoIteration(tcs, whileTask);
                }
            }, TaskScheduler.Current);
        }

        internal struct VoidTypeStruct { }  // See Footnote #1

        public static Task TimeoutAfter(this Task task, int millisecondsTimeout)
        {
            // Short-circuit #1: infinite timeout or task already completed
            if (task.IsCompleted || (millisecondsTimeout == Timeout.Infinite))
            {
                // Either the task has already completed or timeout will never occur.
                // No proxy necessary.
                return task;
            }

            // tcs.Task will be returned as a proxy to the caller
            TaskCompletionSource<VoidTypeStruct> tcs =
                new TaskCompletionSource<VoidTypeStruct>();

            // Short-circuit #2: zero timeout
            if (millisecondsTimeout == 0)
            {
                // We've already timed out.
                tcs.SetException(new TimeoutException());
                return tcs.Task;
            }

            // Set up a timer to complete after the specified timeout period
            Timer timer = new Timer(state =>
            {
                // Recover your state information
                var myTcs = (TaskCompletionSource<VoidTypeStruct>)state;

                // Fault our proxy with a TimeoutException
                myTcs.TrySetException(new TimeoutException());
            }, tcs, millisecondsTimeout, Timeout.Infinite);

            var tuple = new { timer, tcs };

            // Wire up the logic for what happens when source task completes
            task.ContinueWith((antecedent) =>
                {
                    // Recover our state data
                    // Cancel the Timer
                    tuple.timer.Dispose();

                    // Marshal results to proxy
                    MarshalTaskResults(antecedent, tuple.tcs);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return tcs.Task;
        }


        public static Task<T> TimeoutAfter<T>(this Task<T> task, int millisecondsTimeout)
        {
            // Short-circuit #1: infinite timeout or task already completed
            if (task.IsCompleted || (millisecondsTimeout == Timeout.Infinite))
            {
                // Either the task has already completed or timeout will never occur.
                // No proxy necessary.
                return task;
            }

            // tcs.Task will be returned as a proxy to the caller
            TaskCompletionSource<T> tcs =
                new TaskCompletionSource<T>();

            // Short-circuit #2: zero timeout
            if (millisecondsTimeout == 0)
            {
                // We've already timed out.
                tcs.SetException(new TimeoutException());
                return tcs.Task;
            }

            // Set up a timer to complete after the specified timeout period
            Timer timer = new Timer(state =>
            {
                // Recover your state information
                var myTcs = (TaskCompletionSource<T>)state;

                // Fault our proxy with a TimeoutException
                myTcs.TrySetException(new TimeoutException());
            }, tcs, millisecondsTimeout, Timeout.Infinite);

            var tuple = new { timer, tcs };

            // Wire up the logic for what happens when source task completes
            task.ContinueWith((antecedent) =>
                {
                    // Recover our state data
                    // Cancel the Timer
                    tuple.timer.Dispose();

                    // Marshal results to proxy
                    MarshalTaskResults(antecedent, tuple.tcs);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return tcs.Task;
        }


        internal static void MarshalTaskResults<TResult>(Task source, TaskCompletionSource<TResult> proxy)
        {
            switch (source.Status)
            {
                case TaskStatus.Faulted:
                    proxy.TrySetException(source.Exception);
                    break;
                case TaskStatus.Canceled:
                    proxy.TrySetCanceled();
                    break;
                case TaskStatus.RanToCompletion:
                    Task<TResult> castedSource = source as Task<TResult>;
                    proxy.TrySetResult(castedSource == null ? default(TResult) : castedSource.Result);
                    break;
            }
        }
    }
}