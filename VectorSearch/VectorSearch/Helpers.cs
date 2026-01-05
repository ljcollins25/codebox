#nullable enable
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VectorSearch;

/// <summary>
/// Type marker for generic operations.
/// </summary>
public struct Type<T>()
    where T : allows ref struct;

/// <summary>
/// Nullable reference wrapper for ref struct compatibility.
/// </summary>
public ref struct NRef<T>(ref T value)
{
    public bool IsValid = true;
    public ref T Value = ref value;
}

/// <summary>
/// Tuple wrapper for ref struct compatibility.
/// </summary>
public ref struct RefTuple<T1, T2>(T1 item1, T2 item2)
    where T1 : allows ref struct
    where T2 : allows ref struct
{
    public T1 Item1 = item1;
    public T2 Item2 = item2;
}

/// <summary>
/// Reference wrapper for passing by reference into lambdas.
/// </summary>
public ref struct Ref<T>(ref T value)
{
    public ref T Value = ref value;
}

/// <summary>
/// Static helper methods.
/// </summary>
public static class Helpers
{
    public static T Invoke<T>(Func<T> func) => func();

    public static T Plus<T>(this T value) where T : INumber<T> => value + T.One;

    public static int BinarySearchIndexOf<T>(this ReadOnlySpan<T> span, T value, IComparer<T> comparer)
    {
        var index = span.BinarySearch(value, comparer);
        return index >= 0 ? index : ~index;
    }

    public static void MaxWith<T>(this ref T a, in T b)
        where T : struct, IComparisonOperators<T, T, bool>
    {
        if (b > a) a = b;
    }

    public static void MinWith<T>(this ref T a, in T b)
        where T : struct, IComparisonOperators<T, T, bool>
    {
        if (b < a) a = b;
    }

    public static RefTuple<T1, T2> RefTuple<T1, T2>(T1 item1, T2 item2)
        where T1 : allows ref struct
        where T2 : allows ref struct
        => new(item1, item2);

    public static T Default<T>(this T t) where T : allows ref struct => default(T)!;
    public static T Default<T>(Type<T> t) where T : allows ref struct => default(T)!;

    public static Ref<T> Ref<T>(ref T value) => new(ref value);
    public static NRef<T> NRef<T>(ref T value) => new(ref value);
    public static NRef<T> DefaultNRef<T>(Type<T> t = default) => default;

    public static Span<T> Span<T>(Span<T> span) => span;
    public static SpanList<T> SpanList<T>(Span<T> span) => new(span);
    public static Type<T> Type<T>() => default;

    public static void For(bool parallel, int fromInclusive, int toExclusive, Action<int> body)
    {
        if (parallel)
        {
            Parallel.For(fromInclusive, toExclusive, body);
        }
        else
        {
            for (int i = fromInclusive; i < toExclusive; i++)
            {
                body(i);
            }
        }
    }

    public static int SizeOf<T>() where T : unmanaged
        => MemoryMarshal.AsBytes(stackalloc T[1]).Length;

    public static ref ulong UnsafeAsULong<T>(ref T value) where T : unmanaged
        => ref Unsafe.As<T, ulong>(ref value);

    public static ulong UnsafeAsULong<T>(T value) where T : unmanaged
        => Unsafe.As<T, ulong>(ref value);

    public static Exception NotImplemented() => throw new NotImplementedException();

    public static float Squared(this float f) => f * f;

    public static int? AsCompare(this int i) => i == 0 ? null : i;

    public static bool TrueVar<T>(out T value, T input)
    {
        value = input;
        return true;
    }
}

/// <summary>
/// Atomic operations helper.
/// </summary>
public static class Atomic
{
    public static bool TryCompareExchange(ref int location, int value, int comparand)
        => Interlocked.CompareExchange(ref location, value, comparand) == comparand;

    public static bool TryCompareExchange(ref long location, long value, long comparand)
        => Interlocked.CompareExchange(ref location, value, comparand) == comparand;

    public static bool TryCompareExchange(ref ulong location, ulong value, ulong comparand)
        => Interlocked.CompareExchange(ref location, value, comparand) == comparand;

    public static T Create<T>(ref T location, T value) where T : class
        => Interlocked.CompareExchange(ref location, value, null!) ?? value;

    public static void Max(ref int location, int value)
    {
        while (Helpers.TrueVar(out var current, location)
            && value > current
            && !TryCompareExchange(ref location, value, current))
        { }
    }

    public static void Max(ref long location, long value)
    {
        while (Helpers.TrueVar(out var current, location)
            && value > current
            && !TryCompareExchange(ref location, value, current))
        { }
    }

    public static void Min(ref ulong location, ulong value)
    {
        while (Helpers.TrueVar(out var current, location)
            && value < current
            && !TryCompareExchange(ref location, value, current))
        { }
    }
}
