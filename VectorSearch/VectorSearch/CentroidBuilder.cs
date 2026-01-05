#nullable disable
#nullable enable annotations
using System.Numerics;
using System.Numerics.Tensors;

namespace VectorSearch;

/// <summary>
/// Helper for computing centroids incrementally.
/// </summary>
public ref struct CentroidBuilder(Span<float> vector)
{
    public int Count { get; private set; }
    public Span<float> Vector = vector;

    public void Add(ReadOnlySpan<float> component)
    {
        TensorPrimitives.Add(Vector, component, Vector);
        Count++;
    }

    public static implicit operator CentroidBuilder(Span<float> values) => new(values);

    public void Reset()
    {
        Vector.Clear();
        Count = 0;
    }

    public Span<float> Build()
    {
        if (Count > 0)
        {
            TensorPrimitives.Divide(Vector, Count, Vector);
        }
        return Vector;
    }
}
