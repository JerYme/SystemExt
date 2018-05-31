namespace System.Collections.Generic
{
    public class LambdaComparer<T> : IComparer<T>
    {
        private readonly Func<T, T, int> _comparer;

        public LambdaComparer(Func<T, T, int> comparer)
        {
            _comparer = comparer ?? ((t1, t2) => Comparer<T>.Default.Compare(t1, t2));
        }

        public int Compare(T x, T y) => _comparer(x, y);
    }
}