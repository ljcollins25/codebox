#nullable enable
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static VectorSearch.Helpers;

namespace VectorSearch;

public ref struct Ref<T>
{
    public ref T Value;

    public unsafe Ref(ref T value)
    {
        Value = ref value;
    }
}

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
/// Func-like interface for ref struct compatibility.
/// </summary>
public interface IFuncInvoke<TArg1, TArg2, TResult>
    where TArg1 : allows ref struct
    where TArg2 : allows ref struct
    where TResult : allows ref struct
{
    TResult Invoke(TArg1 arg1, TArg2 arg2);
}

public class DisplayLazy<T>(Func<T> factory)
{
    public T Value => field ??= factory();

    public override string ToString()
    {
        return Value!.ToString()!;
    }
}

/// <summary>
/// Static helper methods.
/// </summary>
public static class Helpers
{
    public static (T1, T2, T3) SelectWith<T1, T2, T3>(this (T1, T2) t, Func<(T1, T2), T3> select)
    {
        return (t.Item1, t.Item2, select(t));
    }

    public static (T1, T2, T3, T4) SelectWith<T1, T2, T3, T4>(this (T1, T2, T3) t, Func<(T1, T2, T3), T4> select)
    {
        return (t.Item1, t.Item2, t.Item3, select(t));
    }

    public static DisplayLazy<T> DisplayLazy<T>(Func<T> func) => new(func);

    public static IEnumerable<TSource> ExceptBy<TSource, TKey>(this IEnumerable<TSource> first, IEnumerable<TSource> second, Func<TSource, TKey> keySelector)
    {
        return first.ExceptBy(second.Select(keySelector), keySelector);
    }

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

    public static void For<TLocal>(bool parallel, int fromInclusive, int toExclusive, Func<TLocal> localInit, Func<int, ParallelLoopState, TLocal, TLocal> body, Action<TLocal> localFinally)
    {
        if (parallel)
        {
            Parallel.For(fromInclusive, toExclusive, localInit, body, localFinally);
        }
        else
        {
            var options = new ParallelOptions()
            {
                MaxDegreeOfParallelism = 1
            };
            Parallel.For(fromInclusive, toExclusive, options, localInit, body, localFinally);
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

    public delegate void ExpandSpan<T, TData>(ref Span<T> span, int requiredSize, TData data)
        where TData : allows ref struct;

    /// <summary>
    /// Inserts an item into a sorted span, maintaining sorted order up to maxCapacity.
    /// Returns the insertion index, or -1 if the item was not inserted.
    /// </summary>
    /// <param name="span">Reference to the current span (may be replaced if expanded).</param>
    /// <param name="count">Reference to the current count of items in the span.</param>
    /// <param name="maxCapacity">Maximum number of items to keep.</param>
    /// <param name="item">The item to insert.</param>
    /// <param name="comparer">Comparer to determine sort order (less than = better/earlier).</param>
    /// <param name="expand">Delegate to expand the span if span.Length &lt; capacity. Called with current span and required minimum length.</param>
    public static int SortedInsert<T, TData>(
        Span<T> span,
        int maxCapacity,
        in T item,
        IComparer<T> comparer,
        TData data,
        ExpandSpan<T, TData> expand)
        where TData : allows ref struct
    {
        var count = span.Length;

        // Hot-path cutoff: if at capacity and item is not better than worst
        if (count == maxCapacity && comparer.Compare(item, span[count - 1]) >= 0)
            return -1;

        // Expand span if needed
        if (span.Length < maxCapacity)
        {
            expand.Invoke(ref span, count + 1, data);
        }

        // Binary search insertion point
        int lo = 0, hi = count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (comparer.Compare(item, span[mid]) < 0)
                hi = mid;
            else
                lo = mid + 1;
        }

        ShiftInsert(span, maxCapacity, item, count, lo);

        return lo;
    }

    public static void ShiftInsert<T>(Span<T> span, int maxCapacity, T item, int count, int index)
    {
        // Shift elements to make room
        int move = Math.Min(count, maxCapacity - 1) - index;
        if (move > 0)
            span.Slice(index, move).CopyTo(span.Slice(index + 1));

        span[index] = item;
    }

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
