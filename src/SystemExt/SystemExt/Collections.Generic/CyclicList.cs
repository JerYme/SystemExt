using System.Linq;
using static System.Math;

namespace System.Collections.Generic
{
    public class CyclicList<T> : IList<T>
    {
        private readonly T[] _array;
        private readonly int _startIndex;
        private readonly int _count;

        public CyclicList(T[] array, int startIndex, int count)
        {
            _array = array;
            _startIndex = startIndex;
            _count = count;
        }

        private IEnumerable<T> Range(int index, int count)
        {
            var l = count + index;
            for (int i = index; i < l; i++)
            {
                yield return _array[i];
            }
        }

        private IEnumerable<T> GetEnumerable()
            => (_startIndex < _array.Length ? Range(_startIndex, Min(_array.Length - _startIndex, _count)) : Enumerable.Empty<T>())
        .Concat(_startIndex > 0 && _array.Length - _startIndex < _count ? Range(0, Min(_array.Length - _startIndex - _count, _startIndex)) : Enumerable.Empty<T>())
        ;

        public IEnumerator<T> GetEnumerator() => GetEnumerable().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void Add(T item)
        {
            throw new NotSupportedException();
        }

        public void Clear()
        {
            throw new NotSupportedException();
        }

        public bool Contains(T item) => GetEnumerable().Contains(item);

        public void CopyTo(T[] array, int arrayIndex)
        {
            foreach (var t in GetEnumerable())
            {
                array[arrayIndex++]=t;
            }
        }

        public bool Remove(T item)
        {
            throw new NotSupportedException();
        }

        public int Count => _count;
        public bool IsReadOnly => true;
        public int IndexOf(T item)
        {
            throw new NotImplementedException();
        }

        public void Insert(int index, T item)
        {
            throw new NotSupportedException();
        }

        public void RemoveAt(int index)
        {
            throw new NotSupportedException();
        }

        public T this[int index]
        {
            get { throw new NotSupportedException(); }
            set { throw new NotImplementedException(); }
        }
    }
}