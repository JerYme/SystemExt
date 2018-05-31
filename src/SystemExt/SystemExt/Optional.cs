namespace System
{
    public struct Optional<T>
    {
        private readonly bool _set;
        public readonly T Value;

        public static readonly Optional<T> NotSet = new Optional<T>(false, default(T));

        private Optional(bool set, T value)
        {
            _set = set;
            Value = value;
        }


        public static implicit operator bool(Optional<T> optional) => optional._set;
        public static implicit operator T(Optional<T> optional) => optional.Value;
        public static implicit operator Optional<T>(T v) => new Optional<T>(true, v);
    }
}