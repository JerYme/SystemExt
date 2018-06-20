using System.Diagnostics;
using System.Threading.Tasks;

namespace System.Threading
{
    /// <summary>
    /// 
    /// </summary>
    public static class TaskEx
    {
        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public static Task<T> FromCanceled<T>(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<T>(cancellationToken);
            tcs.TrySetCanceled();
            return tcs.Task;
        }

    }
}