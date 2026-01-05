#nullable enable
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.Runtime.CompilerServices;

namespace VectorSearch;

/// <summary>
/// Helper type for avoiding allocations while building arrays.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
public class ArrayBuilder<T>
{
    private const int DefaultCapacity = 4;

    private T[] _array = null!;
    private int _count;

    /// <summary>
    /// Initializes the <see cref="ArrayBuilder{T}"/> with a specified capacity.
    /// </summary>
    public ArrayBuilder(int capacity = DefaultCapacity, int? length = 0)
    {
        Debug.Assert(capacity >= 0);
        if (capacity > 0)
        {
            _array = new T[capacity];
        }
        _count = length ?? capacity;
    }

    public ArrayBuilder(T[] initialArray)
    {
        _array = initialArray;
    }

    /// <summary>
    /// Gets the number of items this instance can store without re-allocating.
    /// </summary>
    public int Capacity => _array?.Length ?? 0;

    /// <summary>Gets the current underlying array.</summary>
    public T[]? Buffer => _array;

    /// <summary>
    /// Gets the number of items in the array currently in use.
    /// </summary>
    public int Count => _count;

    /// <summary>
    /// Gets or sets the number of items in the array currently in use.
    /// </summary>
    public int Length { get => _count; set => SetLength(value); }

    /// <summary>
    /// Gets or sets the item at a certain index in the array.
    /// </summary>
    public ref T this[int index]
    {
        get
        {
            Contract.Assert(unchecked((uint)index < (uint)_count));
            return ref _array![index];
        }
    }

    /// <summary>
    /// Adds an item to the backing array, resizing it if necessary.
    /// </summary>
    public void Add(T item) => AddAndGet(item);

    public void RemoveAt(int index)
    {
        CheckRange(index - 1);
        if (index < _count)
        {
            Array.Copy(_array, index + 1, _array, index, _count - index);
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
            Array.Copy(_array, index, _array, index + 1, _count - index);
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

    public Span<T> Span => _array.AsSpan(0, _count);

    public ArraySegment<T> Segment => new(_array, 0, _count);

    /// <summary>
    /// Makes the instance empty WITHOUT clearing the contents
    /// </summary>
    public void Reset() => _count = 0;

    /// <summary>
    /// Makes the instance empty AND clears the contents
    /// </summary>
    public void Clear()
    {
        Span.Clear();
        Reset();
    }

    public ref T First => ref Span[0];
    public ref T Last => ref Span[_count - 1];
    public T[] UnderlyingArrayUnsafe => _array;

    /// <summary>
    /// Creates an array from the contents of this builder.
    /// </summary>
    public T[] ToArray()
    {
        if (_count == 0)
        {
            return Array.Empty<T>();
        }
        return Span.ToArray();
    }

    /// <summary>
    /// Adds an item to the backing array, without checking if there is room.
    /// </summary>
    public ref T UncheckedAdd(T item)
    {
        Debug.Assert(_count < Capacity);
        ref var slot = ref _array![_count++];
        slot = item;
        return ref slot;
    }

    public void Resize(int newLength)
    {
        Array.Resize(ref _array, newLength);
        _count = newLength;
    }

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

        int capacity = Capacity;
        int nextCapacity = capacity == 0 ? DefaultCapacity : 2 * capacity;

        if ((uint)nextCapacity > (uint)Array.MaxLength)
        {
            nextCapacity = Math.Max(capacity + 1, Array.MaxLength);
        }

        nextCapacity = Math.Max(nextCapacity, minimum);

        T[] next = new T[nextCapacity];
        if (_count > 0)
        {
            Array.Copy(_array!, next, _count);
        }
        _array = next;

        return true;
    }
}
