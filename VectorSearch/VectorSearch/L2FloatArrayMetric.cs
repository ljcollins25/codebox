#nullable enable
using System.Numerics;
using System.Numerics.Tensors;

namespace VectorSearch;

/// <summary>
/// L2 (Euclidean) distance metric for float arrays.
/// Returns squared L2 distance for efficiency.
/// </summary>
public sealed class L2FloatArrayMetric : IMetricModel
{
    private readonly int _dim;

    public int Dimension => _dim;

    public L2FloatArrayMetric(int dimension)
    {
        if (dimension <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimension));
        _dim = dimension;
    }

    /// <summary>
    /// Computes the squared L2 distance between two vectors.
    /// </summary>
    public float Distance(in ReadOnlySpan<float> a, in ReadOnlySpan<float> b)
    {
        // TODO: Precompute norm of vectors?
        float sum = 0f;
        int i = 0;
        int simdWidth = Vector<float>.Count;

        // SIMD loop
        for (; i <= _dim - simdWidth; i += simdWidth)
        {
            var va = new Vector<float>(a.Slice(i, simdWidth));
            var vb = new Vector<float>(b.Slice(i, simdWidth));
            var diff = va - vb;
            sum += Vector.Dot(diff, diff);
        }

        // Remainder
        for (; i < _dim; i++)
        {
            float d = a[i] - b[i];
            sum += d * d;
        }

        return sum;
    }

    /// <summary>
    /// Computes the dot product of two vectors.
    /// </summary>
    public float Dot(ReadOnlySpan<float> v, ReadOnlySpan<float> dir)
    {
        int i = 0;
        int simdWidth = Vector<float>.Count;
        Vector<float> acc = Vector<float>.Zero;

        // SIMD loop
        for (; i <= _dim - simdWidth; i += simdWidth)
        {
            var vv = new Vector<float>(v.Slice(i, simdWidth));
            var dd = new Vector<float>(dir.Slice(i, simdWidth));
            acc += vv * dd;
        }

        // Horizontal sum
        float dot = 0f;
        for (int j = 0; j < Vector<float>.Count; j++)
            dot += acc[j];

        // Remainder
        for (; i < _dim; i++)
            dot += v[i] * dir[i];

        return dot;
    }

    /// <summary>
    /// Computes the L2 norm (magnitude) of a vector.
    /// </summary>
    public float Norm(ReadOnlySpan<float> v)
        => MathF.Sqrt(Dot(v, v));

    /// <summary>
    /// Normalizes a vector in place.
    /// </summary>
    public void Normalize(Span<float> v, float? norm = null)
    {
        float n = norm ?? Norm(v);
        if (n <= 0f) return;

        float inv = 1f / n;
        int i = 0;
        int len = v.Length;
        int w = Vector<float>.Count;
        var scale = new Vector<float>(inv);

        for (; i <= len - w; i += w)
        {
            var vv = new Vector<float>(v.Slice(i, w));
            (vv * scale).CopyTo(v.Slice(i, w));
        }

        for (; i < len; i++)
            v[i] *= inv;
    }

    /// <summary>
    /// Subtracts a scaled vector: v = v - u * scale
    /// </summary>
    public static void SubtractScaled(Span<float> v, ReadOnlySpan<float> u, float scale)
    {
        int i = 0;
        int len = v.Length;
        int w = Vector<float>.Count;
        var s = new Vector<float>(scale);

        for (; i <= len - w; i += w)
        {
            var vv = new Vector<float>(v.Slice(i, w));
            var uu = new Vector<float>(u.Slice(i, w));
            (vv - uu * s).CopyTo(v.Slice(i, w));
        }

        for (; i < len; i++)
            v[i] -= u[i] * scale;
    }
}
