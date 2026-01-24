#nullable enable
using System.Numerics;

namespace VectorSearch;

/// <summary>
/// Cosine distance metric for float arrays.
/// Returns cosine distance: 1 - cosine_similarity
/// For L2-normalized vectors, cosine distance relates to L2 distance: d_cos = 1 - (1 - d_l2²/2)
/// </summary>
public sealed class CosineFloatArrayMetric : IMetricModel
{
    private readonly int _dim;

    public int Dimension => _dim;

    public CosineFloatArrayMetric(int dimension)
    {
        if (dimension <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimension));
        _dim = dimension;
    }

    /// <summary>
    /// Computes the cosine distance (1 - cosine_similarity) between two vectors.
    /// Assumes vectors are L2-normalized; if not, the result may not be in [0, 2].
    /// </summary>
    public float Distance(in ReadOnlySpan<float> a, in ReadOnlySpan<float> b)
    {
        // For normalized vectors: cosine_similarity = dot(a, b)
        // cosine_distance = 1 - cosine_similarity
        float dot = Dot(a, b);
        return 1f - dot;
    }

    /// <summary>
    /// Computes the dot product of two vectors.
    /// For L2-normalized vectors, this equals cosine similarity.
    /// </summary>
    public float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int i = 0;
        int simdWidth = Vector<float>.Count;
        Vector<float> acc = Vector<float>.Zero;

        // SIMD loop
        for (; i <= _dim - simdWidth; i += simdWidth)
        {
            var va = new Vector<float>(a.Slice(i, simdWidth));
            var vb = new Vector<float>(b.Slice(i, simdWidth));
            acc += va * vb;
        }

        // Horizontal sum
        float dot = 0f;
        for (int j = 0; j < Vector<float>.Count; j++)
            dot += acc[j];

        // Remainder
        for (; i < _dim; i++)
            dot += a[i] * b[i];

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
}
