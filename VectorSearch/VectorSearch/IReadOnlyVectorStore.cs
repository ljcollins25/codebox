#nullable enable

namespace VectorSearch;

/// <summary>
/// Read-only interface for accessing vectors by their IDs.
/// </summary>
public interface IReadOnlyVectorStore
{
    /// <summary>
    /// The dimensionality of vectors in this store.
    /// </summary>
    int Dimensions { get; }

    /// <summary>
    /// The number of vectors in the store.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Gets the vector data for the given ID.
    /// </summary>
    ReadOnlySpan<float> GetVector(VectorId index);
}
