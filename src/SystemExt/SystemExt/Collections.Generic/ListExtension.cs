namespace System.Collections.Generic
{
    public static class ListExtension
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static bool IsNullOrEmpty<T>(this string value)
            => ReferenceEquals(null, value) || value.Length == 0;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static bool IsNullOrEmpty<T>(this T[] array)
            => ReferenceEquals(null, array) || array.Length == 0;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static bool IsNullOrEmpty<T>(this IEnumerable<T> enumerable)
        {
            if (ReferenceEquals(null, enumerable)) return true;
            using (var enumerator = enumerable.GetEnumerator())
                return !enumerator.MoveNext();
        }

        public static IEnumerable<Tuple<T,T>> SelectTuple2<T>(this IEnumerable<T> enumerable)
        {
            using(var enumerator = enumerable.GetEnumerator())
            {
                if (!enumerator.MoveNext()) yield break;
                var previous = enumerator.Current;
                while (enumerator.MoveNext())
                {
                    var current = enumerator.Current;
                    yield return new Tuple<T, T>(previous, current);
                    previous = current;
                }
            }
        }

        public static IEnumerable<IList<T>> SelectGroupSize<T>(this IEnumerable<T> enumerable, int size, int step = 1)
        {
            var group = new T[size];

            int i = 0;
            int ci = 0;
            foreach (var t in enumerable)
            {
                group[i] = t;
                ++ci;
                if (ci - size >= 0 && ci % size % step == 0) yield return new CyclicList<T>(group, i - size, size);
                ++i;
                if (i == size) i = 0;
            }
        }

        public static T? FirstOrNull<T>(this IEnumerable<T> enumerable, Func<T, bool> predicate = null) where T : struct
        {
            if (ReferenceEquals(null, enumerable)) return null;
            foreach (var item in enumerable)
            {
                if (predicate?.Invoke(item) != false) return item;
            }
            return null;
        }

        public static int BinarySearch<T>(this IList<T> list, T value, IComparer<T> comparer = null)
        {
            if (list == null) throw new ArgumentNullException(nameof(list));
            if (list is List<T> l) return l.BinarySearch(value, comparer);

            comparer = comparer ?? Comparer<T>.Default;

            int lower = 0;
            int upper = list.Count - 1;

            while (lower <= upper)
            {
                int middle = lower + (upper - lower) / 2;
                int comparisonResult = comparer.Compare(value, list[middle]);
                if (comparisonResult < 0)
                {
                    upper = middle - 1;
                }
                else if (comparisonResult > 0)
                {
                    lower = middle + 1;
                }
                else
                {
                    return middle;
                }
            }

            return ~lower;
        }

        public static int BinarySearch<T>(this IList<T> list, T value, Func<T, T, int> comparer)
        {
            if (list == null) throw new ArgumentNullException("enumerable");
            if (list is List<T> l) return l.BinarySearch(value, new LambdaComparer<T>(comparer));

            comparer = comparer ?? ((t1, t2) => Comparer<T>.Default.Compare(t1, t2));

            int lower = 0;
            int upper = list.Count - 1;

            while (lower <= upper)
            {
                int middle = lower + (upper - lower) / 2;
                int comparisonResult = comparer(value, list[middle]);
                if (comparisonResult < 0)
                {
                    upper = middle - 1;
                }
                else if (comparisonResult > 0)
                {
                    lower = middle + 1;
                }
                else
                {
                    return middle;
                }
            }

            return ~lower;
        }
    }
}