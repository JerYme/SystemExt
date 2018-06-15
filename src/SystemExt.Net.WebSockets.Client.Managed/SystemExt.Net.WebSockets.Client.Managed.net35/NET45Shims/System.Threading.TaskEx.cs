using System.Threading.Tasks;

namespace System.Threading
{
    public static class TaskEx
    {
        public static Task TaskCompleted;
        public static Task<bool> TaskContinue;
        public static Task<bool> TaskBreak;

        static TaskEx()
        {
            TaskCompleted = FromResult(0);
            TaskContinue = FromResult(true);
            TaskBreak = FromResult(false);
        }

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
            => task.ContinueWith(t => @finally(), TaskContinuationOptions.AttachedToParent);

        public static Task<T> FinallyWith<T>(this Task<T> task, Action @finally)
            => task.ContinueWith(t =>
            {
                @finally();
                return t.Result;
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


        // without async/await
        public static Task AsyncLoopTask(Func<Task<bool>> @newTask)
        {
            var tcs = new TaskCompletionSource<int>();
            var iteration = DoIteration(tcs, newTask);
            return iteration.ContinueWith(t => tcs.Task.Wait(), TaskScheduler.FromCurrentSynchronizationContext());
        }

        private static Task DoIteration(TaskCompletionSource<int> tcs, Func<Task<bool>> whileTask)
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
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        public static Task<T> AsyncLoopTask<T>(Func<Task<Flow<T>>> whileTask)
        {
            var root = Task.Factory.StartNew(() =>
            {
                while (true)
                {
                    var t = whileTask();
                    t.Start(TaskScheduler.FromCurrentSynchronizationContext());
                    if (t.Result) continue;
                    return t.Result.Value;
                }
            });
            return root;

            //var tcs = new TaskCompletionSource<T>();
            //var iteration = DoIteration(tcs, whileTask);
            //return iteration.ContinueWith(t => tcs.Task, TaskScheduler.FromCurrentSynchronizationContext()).Unwrap();
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
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}