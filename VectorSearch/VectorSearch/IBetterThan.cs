#nullable enable

namespace VectorSearch;

/// <summary>
/// Interface for comparing two items to determine which is "better".
/// </summary>
public interface IBetterThan<T>
{
    bool IsBetter(in T a, in T b);
}
