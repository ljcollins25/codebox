#nullable disable
#nullable enable annotations
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.Runtime.CompilerServices;

namespace VectorSearch;

/// <summary>
/// Stack-allocated list backed by a span. Does not support resizing.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
public ref struct SpanList<T>
{
    private Span<T> _array;
    private int _count;

    public SpanList(Span<T> array, int? length = 0)
    {
        _array = array;
        _count = length ?? array.Length;
    }

    public int Capacity => _array.Length;
    public Span<T> Buffer => _array;
    public int Count => _count;
    public int Length { get => _count; set => SetLength(value); }

    public ref T this[int index]
    {
        get
        {
            Contract.Assert(unchecked((uint)index < (uint)_count));
            return ref _array![index];
        }
    }

    public void Add(T item) => AddAndGet(item);

    public void RemoveAt(int index)
    {
        CheckRange(index - 1);
        if (index < _count)
        {
            _array.Slice(index + 1, _count - index).CopyTo(_array.Slice(index));
        }
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            _array[_count] = default!;
        }
        _count--;
    }

    public ref T AddAndGet(T item)
    {
        if (_count == Capacity)
        {
            EnsureCapacity(_count + 1);
        }
        return ref UncheckedAdd(item);
    }

    public void AddRange(ReadOnlySpan<T> items)
    {
        EnsureCapacity(_count + items.Length);
        var priorCount = _count;
        _count += items.Length;
        items.CopyTo(Span.Slice(priorCount));
    }

    public void ForceInsert(int index, T item)
    {
        EnsureLength(index + 1);
        _array[index] = item;
    }

    public void Insert(int index, T item)
    {
        CheckRange(index);
        if (_count == _array.Length) EnsureCapacity(_count + 1);
        if (index < _count)
        {
            _array.Slice(index, _count - index).CopyTo(_array.Slice(index + 1));
        }
        _array[index] = item;
        _count++;
    }

    private void CheckRange(int index)
    {
        Contract.Check((uint)index <= (uint)_count)?.Assert($"{index} out of range. List length = {_count}");
    }

    public bool TryAdd(T value)
    {
        if (_count < Capacity)
        {
            UncheckedAdd(value);
            return true;
        }
        return false;
    }

    public Span<T> Span => _array.Slice(0, _count);

    public void Reset() => _count = 0;

    public void Clear()
    {
        Span.Clear();
        Reset();
    }

    public ref T First => ref Span[0];
    public ref T Last => ref Span[_count - 1];

    public T[] ToArray()
    {
        if (_count == 0)
        {
            return Array.Empty<T>();
        }
        return Span.ToArray();
    }

    public ref T UncheckedAdd(T item)
    {
        Debug.Assert(_count < Capacity);
        ref var slot = ref _array![_count++];
        slot = item;
        return ref slot;
    }

    public void Resize(int newLength) => _count = newLength;

    public bool SetLength(int newLength)
    {
        var result = EnsureCapacity(newLength);
        _count = newLength;
        return result;
    }

    public bool EnsureLength(int minimum)
    {
        var result = EnsureCapacity(minimum);
        _count = Math.Max(_count, minimum);
        return result;
    }

    public bool EnsureCapacity(int minimum)
    {
        if (minimum <= Capacity) return false;
        throw new NotSupportedException("SpanList does not support resizing.");
    }
}
