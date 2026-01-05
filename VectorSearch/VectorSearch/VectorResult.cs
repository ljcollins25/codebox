#nullable enable
using System.Numerics;

namespace VectorSearch;

/// <summary>
/// Represents a query result with associated metadata and distance.
/// </summary>
public readonly record struct VectorResult<TMeta>(TMeta Data, float Distance)
    : IComparisonOperators<VectorResult<TMeta>, VectorResult<TMeta>, bool>, IComparable<VectorResult<TMeta>>
{
    public readonly float Distance = Distance;
    public readonly TMeta Data = Data;

    public override string ToString() => $"(D: {(int)Distance,4}, V: {Data,20})";

    public int CompareTo(VectorResult<TMeta> other) => Distance.CompareTo(other.Distance);

    public static readonly VectorResult<TMeta> MaxValue = new(default!, float.MaxValue);

    public static bool operator <(VectorResult<TMeta> left, VectorResult<TMeta> right)
        => left.Distance < right.Distance;

    public static bool operator >(VectorResult<TMeta> left, VectorResult<TMeta> right)
        => left.Distance > right.Distance;

    public static bool operator <=(VectorResult<TMeta> left, VectorResult<TMeta> right)
        => left.Distance <= right.Distance;

    public static bool operator >=(VectorResult<TMeta> left, VectorResult<TMeta> right)
        => left.Distance >= right.Distance;
}

/// <summary>
/// Represents a vector entry with associated metadata.
/// </summary>
public readonly struct VectorEntry<TVector, TMeta>
{
    public readonly TVector Vector;
    public readonly TMeta Meta;

    public VectorEntry(in TVector vector, in TMeta meta)
    {
        Vector = vector;
        Meta = meta;
    }
}
