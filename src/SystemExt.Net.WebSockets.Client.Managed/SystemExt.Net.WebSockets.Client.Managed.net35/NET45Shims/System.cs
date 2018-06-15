using System.Threading;

namespace System
{
  //public struct Expected<T>
  //{
  //  private readonly bool _b;
  //  public readonly T Value;

  //  public static readonly Expected<T> False;
  //  public static Expected<T> Partial(T value) => new Expected<T>(false, value);

  //  private Expected(bool b, T value)
  //  {
  //    _b = b;
  //    Value = value;
  //  }

  //  public static implicit operator Expected<T>(T value) => new Expected<T>(true, value);
  //  public static implicit operator bool(Expected<T> value) => value._b;
  //}

  public struct Flow<T>
  {
    private readonly bool _b;
    public readonly T Value;

    public static Flow<T> Return(T value) => new Flow<T>(false, value);
    public static Flow<T> Continue() => new Flow<T>(true, default(T));

    private Flow(bool b, T value)
    {
      _b = b;
      Value = value;
    }

    public static implicit operator bool(Flow<T> value) => value._b;
  }

  public struct TimeSpanExt
  {
    public static TimeSpan InfiniteTimeSpan = new TimeSpan(0, 0, 0, 0, -1);
  }
  public struct MonitorExt
  {
    public static bool IsEntered(object o)
    {
      if (Monitor.TryEnter(o, 0))
      {
        Monitor.Exit(o);
        return false;
      }
      return true;
    }
  }
}
