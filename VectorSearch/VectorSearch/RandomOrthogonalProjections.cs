using System.Numerics;

namespace VectorSearch;

/// <summary>
/// Generates random orthogonal projection vectors using Gram-Schmidt orthogonalization.
/// These projections can be used for dimensionality reduction, locality-sensitive hashing,
/// or approximate nearest neighbor search via random hyperplane partitioning.
/// </summary>
public sealed class RandomOrthogonalProjections
{
    private readonly int _dimension;
    private readonly float[][] _projections;

    /// <summary>
    /// Gets the dimensionality of the source vectors.
    /// </summary>
    public int Dimension => _dimension;

    /// <summary>
    /// Gets the number of projection vectors.
    /// </summary>
    public int ProjectionCount => _projections.Length;

    /// <summary>
    /// Gets the projection vectors. Each is a unit vector of length Dimension.
    /// </summary>
    public ReadOnlySpan<float[]> Projections => _projections;

    /// <summary>
    /// Gets a specific projection vector.
    /// </summary>
    public ReadOnlySpan<float> this[int index] => _projections[index];

    /// <summary>
    /// Creates a new set of random orthogonal projection vectors.
    /// </summary>
    /// <param name="dimension">The dimensionality of the vectors to project.</param>
    /// <param name="projectionCount">The number of orthogonal projections to generate.</param>
    /// <param name="seed">Random seed for reproducibility. If null, uses a random seed.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if dimension or projectionCount is less than 1, or if projectionCount exceeds dimension.
    /// </exception>
    public RandomOrthogonalProjections(int dimension, int projectionCount, int? seed = null)
    {
        if (dimension < 1)
            throw new ArgumentOutOfRangeException(nameof(dimension), "Dimension must be at least 1.");
        if (projectionCount < 1)
            throw new ArgumentOutOfRangeException(nameof(projectionCount), "Projection count must be at least 1.");
        if (projectionCount > dimension)
            throw new ArgumentOutOfRangeException(nameof(projectionCount), 
                $"Projection count ({projectionCount}) cannot exceed dimension ({dimension}).");

        _dimension = dimension;
        _projections = new float[projectionCount][];

        var random = seed.HasValue ? new Random(seed.Value) : new Random();
        GenerateOrthogonalProjections(random);
    }

    /// <summary>
    /// Generates orthogonal projection vectors using Gram-Schmidt orthogonalization.
    /// </summary>
    private void GenerateOrthogonalProjections(Random random)
    {
        for (int p = 0; p < _projections.Length; p++)
        {
            var projection = new float[_dimension];
            _projections[p] = projection;

            // Generate random Gaussian vector
            for (int i = 0; i < _dimension; i++)
            {
                projection[i] = SampleGaussian(random);
            }

            // Gram-Schmidt: orthogonalize against all previous projections
            for (int prev = 0; prev < p; prev++)
            {
                var prevProjection = _projections[prev];
                float dot = Dot(projection, prevProjection);
                SubtractScaled(projection, prevProjection, dot);
            }

            // Normalize to unit length
            Normalize(projection);
        }
    }

    /// <summary>
    /// Projects a vector onto all projection directions, returning the dot products.
    /// </summary>
    /// <param name="vector">The vector to project (must have length == Dimension).</param>
    /// <param name="result">Output array to receive projection values (must have length == ProjectionCount).</param>
    public void Project(ReadOnlySpan<float> vector, Span<float> result)
    {
        if (vector.Length != _dimension)
            throw new ArgumentException($"Vector length ({vector.Length}) must match dimension ({_dimension}).", nameof(vector));
        if (result.Length < _projections.Length)
            throw new ArgumentException($"Result length ({result.Length}) must be at least projection count ({_projections.Length}).", nameof(result));

        for (int p = 0; p < _projections.Length; p++)
        {
            result[p] = Dot(vector, _projections[p]);
        }
    }

    /// <summary>
    /// Projects a vector onto all projection directions, returning the dot products.
    /// </summary>
    /// <param name="vector">The vector to project (must have length == Dimension).</param>
    /// <returns>Array of projection values.</returns>
    public float[] Project(ReadOnlySpan<float> vector)
    {
        var result = new float[_projections.Length];
        Project(vector, result);
        return result;
    }

    /// <summary>
    /// Computes the sign bits of projections (useful for LSH).
    /// </summary>
    /// <param name="vector">The vector to project.</param>
    /// <returns>Bit array where bit i is 1 if projection[i] >= 0, else 0.</returns>
    public ulong ProjectToBits(ReadOnlySpan<float> vector)
    {
        if (_projections.Length > 64)
            throw new InvalidOperationException("ProjectToBits only supports up to 64 projections.");

        ulong bits = 0;
        for (int p = 0; p < _projections.Length; p++)
        {
            float dot = Dot(vector, _projections[p]);
            if (dot >= 0)
            {
                bits |= 1UL << p;
            }
        }
        return bits;
    }

    /// <summary>
    /// Samples from a standard Gaussian distribution using Box-Muller transform.
    /// </summary>
    private static float SampleGaussian(Random random)
    {
        // Box-Muller transform
        double u1 = 1.0 - random.NextDouble(); // (0, 1] to avoid log(0)
        double u2 = random.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }

    /// <summary>
    /// Computes the dot product of two vectors (SIMD-optimized).
    /// </summary>
    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int i = 0;
        int len = a.Length;
        int simdWidth = Vector<float>.Count;
        Vector<float> acc = Vector<float>.Zero;

        for (; i <= len - simdWidth; i += simdWidth)
        {
            var va = new Vector<float>(a.Slice(i, simdWidth));
            var vb = new Vector<float>(b.Slice(i, simdWidth));
            acc += va * vb;
        }

        float dot = 0f;
        for (int j = 0; j < Vector<float>.Count; j++)
            dot += acc[j];

        for (; i < len; i++)
            dot += a[i] * b[i];

        return dot;
    }

    /// <summary>
    /// Subtracts a scaled vector: v = v - u * scale (SIMD-optimized).
    /// </summary>
    private static void SubtractScaled(Span<float> v, ReadOnlySpan<float> u, float scale)
    {
        int i = 0;
        int len = v.Length;
        int simdWidth = Vector<float>.Count;
        var s = new Vector<float>(scale);

        for (; i <= len - simdWidth; i += simdWidth)
        {
            var vv = new Vector<float>(v.Slice(i, simdWidth));
            var uu = new Vector<float>(u.Slice(i, simdWidth));
            (vv - uu * s).CopyTo(v.Slice(i, simdWidth));
        }

        for (; i < len; i++)
            v[i] -= u[i] * scale;
    }

    /// <summary>
    /// Normalizes a vector to unit length in place (SIMD-optimized).
    /// </summary>
    private static void Normalize(Span<float> v)
    {
        float norm = MathF.Sqrt(Dot(v, v));
        if (norm <= 1e-12f)
            return;

        float inv = 1f / norm;
        int i = 0;
        int len = v.Length;
        int simdWidth = Vector<float>.Count;
        var scale = new Vector<float>(inv);

        for (; i <= len - simdWidth; i += simdWidth)
        {
            var vv = new Vector<float>(v.Slice(i, simdWidth));
            (vv * scale).CopyTo(v.Slice(i, simdWidth));
        }

        for (; i < len; i++)
            v[i] *= inv;
    }
}
