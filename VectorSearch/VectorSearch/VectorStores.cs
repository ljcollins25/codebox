#nullable enable
using CommunityToolkit.HighPerformance;

namespace VectorSearch;

/// <summary>
/// A vector store backed by a flat array with row-major layout.
/// </summary>
public record VectorMatrix(int Rows, int Dimensions) : IReadOnlyVectorStore
{
    public float[] Array = new float[Rows * Dimensions];

    public Span<float> this[int row] => Array.AsSpan(row * Dimensions, Dimensions);

    public int Count => Rows;

    public ReadOnlySpan<float> GetVector(VectorId index) => this[index];
}

/// <summary>
/// A vector store backed by a 2D array.
/// </summary>
public record VectorBlockArrayStore(float[,] Vectors) : IReadOnlyVectorStore
{
    public int Dimensions { get; } = Vectors.GetLength(1);

    public int Count { get; } = Vectors.GetLength(0);

    public ReadOnlySpan<float> GetVector(VectorId index) => Vectors.GetRowSpan(index);
}
