#nullable enable

namespace VectorSearch;

/// <summary>
/// Interface for computing distances between vectors.
/// </summary>
public interface IMetricModel
{
    /// <summary>
    /// Computes the distance between two vectors.
    /// </summary>
    float Distance(in ReadOnlySpan<float> a, in ReadOnlySpan<float> b);
}
