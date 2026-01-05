#nullable enable
using System.Diagnostics;

namespace VectorSearch;

/// <summary>
/// A sorted top-K collection that maintains elements in sorted order.
/// </summary>
public ref struct SortedTopK<T, TCompare>
    where TCompare : struct, IBetterThan<T>
{
    private Span<T> _buffer;
    private int _count;
    private TCompare _cmp;

    public SortedTopK(Span<T> buffer, int count = 0, TCompare cmp = default)
    {
        _buffer = buffer;
        _count = count;
        _cmp = cmp;
    }

    public int Count => _count;
    public int Capacity => _buffer.Length;

    /// <summary>
    /// Inserts item if it belongs in the top-K.
    /// Returns true if kept.
    /// </summary>
    public int Add(in T item)
    {
        // Hot-path cutoff
        if (_count == _buffer.Length &&
            !_cmp.IsBetter(item, _buffer[_count - 1]))
            return -1;

        // Binary search insertion point
        int lo = 0, hi = _count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_cmp.IsBetter(item, _buffer[mid]))
                hi = mid;
            else
                lo = mid + 1;
        }

        // Shift (memmove)
        int move = Math.Min(_count, _buffer.Length - 1) - lo;
        if (move > 0)
            _buffer.Slice(lo, move).CopyTo(_buffer.Slice(lo + 1));

        _buffer[lo] = item;
        if (_count < _buffer.Length)
            _count++;

        return lo;
    }

    public ReadOnlySpan<T> Items => _buffer.Slice(0, _count);

    public ref readonly T Worst => ref _buffer[_count - 1];
}
