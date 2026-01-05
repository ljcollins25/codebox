#nullable enable

namespace VectorSearch;

/// <summary>
/// Represents a vector identifier in the vector store.
/// Uses 1-based internal storage to distinguish valid (non-zero) from invalid (zero) ids.
/// </summary>
public record struct VectorId(int Index)
{
    public int Index => _index - 1;
    private int _index = Index + 1;
    public bool IsValid => _index != 0;

    public override string ToString() => Index.ToString();

    public static implicit operator int(VectorId id) => id.Index;
    public static implicit operator VectorId(int index) => new(index);
}

/// <summary>
/// A link between a vector and a next pointer for linked list structures.
/// </summary>
public record struct VectorLink(VectorId Vector, int Next)
{
    public bool IsInvalid => Next == 0;
}
