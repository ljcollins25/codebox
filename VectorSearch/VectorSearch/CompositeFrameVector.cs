using System.Text.Json;

namespace VectorSearch;

/// <summary>
/// Combines CLIP image embeddings with perceptual frame vectors into a single
/// composite feature vector for unified similarity search.
/// 
/// The combination uses explicit weighting to control the influence of each component:
/// - Perceptual features (color, texture, edges) contribute 2× weight
/// - CLIP semantic features contribute 1× weight
/// 
/// This allows cosine similarity or L2 distance to be used directly on the
/// combined vector, with perceptual similarity dominating ranking while
/// semantic similarity provides alignment.
/// </summary>
public static class CompositeFrameVector
{
    /// <summary>Default weight for CLIP embedding component.</summary>
    public const float DefaultClipWeight = 1.0f;

    /// <summary>Default weight for perceptual vector component (2× CLIP weight).</summary>
    public const float DefaultPerceptualWeight = 2.0f;

    /// <summary>
    /// Combines a CLIP embedding and a perceptual frame vector into a single composite vector.
    /// 
    /// The weighting scheme ensures perceptual similarity has approximately twice the
    /// influence of semantic (CLIP) similarity in the final cosine similarity computation.
    /// 
    /// Mathematical justification:
    /// - Both input vectors are assumed to be L2-normalized (||v|| = 1)
    /// - Scaling a normalized vector by weight w gives ||w*v|| = w
    /// - After concatenation: combined = [1.0*clip, 2.0*perceptual]
    /// - The squared norm contribution from perceptual is 4× that of CLIP
    /// - Final L2 normalization ensures ||combined|| = 1 for valid cosine similarity
    /// 
    /// The final similarity between two combined vectors decomposes approximately as:
    ///   sim(A, B) ≈ (1/5) * clip_sim + (4/5) * perceptual_sim
    /// giving perceptual features ~4× the influence in the dot product.
    /// </summary>
    /// <param name="clipVector">L2-normalized CLIP embedding (e.g., 512 dims)</param>
    /// <param name="perceptualVector">L2-normalized perceptual frame vector (e.g., 189 dims)</param>
    /// <param name="clipWeight">Weight for CLIP component (default 1.0)</param>
    /// <param name="perceptualWeight">Weight for perceptual component (default 2.0)</param>
    /// <returns>L2-normalized composite vector of length (clipVector.Length + perceptualVector.Length)</returns>
    public static float[] Combine(
        ReadOnlySpan<float> clipVector,
        ReadOnlySpan<float> perceptualVector,
        float clipWeight = DefaultClipWeight,
        float perceptualWeight = DefaultPerceptualWeight)
    {
        int clipDim = clipVector.Length;
        int perceptualDim = perceptualVector.Length;
        int totalDim = clipDim + perceptualDim;

        var combined = new float[totalDim];

        // Step 1: Copy CLIP vector with weight applied
        // Weighting scales the vector's contribution to the final dot product.
        // A weight of 1.0 preserves the original contribution magnitude.
        for (int i = 0; i < clipDim; i++)
        {
            combined[i] = clipWeight * clipVector[i];
        }

        // Step 2: Copy perceptual vector with weight applied
        // A weight of 2.0 means perceptual features contribute 2× in magnitude,
        // which translates to 4× in squared-distance/dot-product terms.
        for (int i = 0; i < perceptualDim; i++)
        {
            combined[clipDim + i] = perceptualWeight * perceptualVector[i];
        }

        // Step 3: Final L2 normalization
        // This is REQUIRED for cosine similarity to work correctly.
        // Without normalization, the combined vector would have ||v|| = sqrt(w1² + w2²) ≈ 2.24,
        // which would break cosine similarity comparisons.
        L2Normalize(combined);

        return combined;
    }

    /// <summary>
    /// Combines a CLIP embedding and a perceptual frame vector, writing to an existing buffer.
    /// Use this overload to avoid allocations in tight loops.
    /// </summary>
    /// <param name="clipVector">L2-normalized CLIP embedding</param>
    /// <param name="perceptualVector">L2-normalized perceptual frame vector</param>
    /// <param name="output">Pre-allocated output buffer (must be clipVector.Length + perceptualVector.Length)</param>
    /// <param name="clipWeight">Weight for CLIP component (default 1.0)</param>
    /// <param name="perceptualWeight">Weight for perceptual component (default 2.0)</param>
    public static void Combine(
        ReadOnlySpan<float> clipVector,
        ReadOnlySpan<float> perceptualVector,
        Span<float> output,
        float clipWeight = DefaultClipWeight,
        float perceptualWeight = DefaultPerceptualWeight)
    {
        int clipDim = clipVector.Length;
        int perceptualDim = perceptualVector.Length;

        if (output.Length < clipDim + perceptualDim)
        {
            throw new ArgumentException(
                $"Output buffer too small. Need {clipDim + perceptualDim}, got {output.Length}",
                nameof(output));
        }

        // Apply weights and copy to output
        for (int i = 0; i < clipDim; i++)
        {
            output[i] = clipWeight * clipVector[i];
        }

        for (int i = 0; i < perceptualDim; i++)
        {
            output[clipDim + i] = perceptualWeight * perceptualVector[i];
        }

        // Final L2 normalization is mandatory
        L2Normalize(output[..(clipDim + perceptualDim)]);
    }

    /// <summary>
    /// Generates composite vectors by combining pre-computed CLIP and perceptual vectors.
    /// </summary>
    /// <param name="clipJsonPath">Path to JSON file with CLIP embeddings (filename/crop -> embedding)</param>
    /// <param name="perceptualJsonPath">Path to JSON file with perceptual descriptors (filename -> descriptor)</param>
    /// <param name="outputJsonPath">Path to output JSON file for composite vectors</param>
    /// <param name="clipCrop">Which CLIP crop to use (default "center")</param>
    /// <param name="clipWeight">Weight for CLIP component</param>
    /// <param name="perceptualWeight">Weight for perceptual component</param>
    public static void GenerateCompositeVectorsToJson(
        string clipJsonPath,
        string perceptualJsonPath,
        string outputJsonPath,
        string clipCrop = "center",
        float clipWeight = DefaultClipWeight,
        float perceptualWeight = DefaultPerceptualWeight)
    {
        // Load CLIP embeddings
        var clipEmbeddings = ClipImageEmbeddings.LoadEmbeddingsFromJson(clipJsonPath);

        // Load perceptual descriptors
        var perceptualDescriptors = PerceptualFrameDescriptor.LoadDescriptorsFromJson(perceptualJsonPath);

        var results = new Dictionary<string, float[]>();

        // For each perceptual descriptor, find matching CLIP embedding and combine
        foreach (var (fileName, perceptualVector) in perceptualDescriptors)
        {
            // Look up corresponding CLIP embedding (format: "filename/crop")
            var clipKey = $"{fileName}/{clipCrop}";
            if (!clipEmbeddings.TryGetValue(clipKey, out var clipVector))
            {
                // Try without crop suffix if not found
                if (!clipEmbeddings.TryGetValue(fileName, out clipVector))
                {
                    continue; // Skip files without CLIP embeddings
                }
            }

            var combined = Combine(clipVector, perceptualVector, clipWeight, perceptualWeight);
            results[fileName] = combined;
        }

        // Write output JSON
        var options = new JsonSerializerOptions { WriteIndented = false };
        var json = JsonSerializer.Serialize(results, options);
        File.WriteAllText(outputJsonPath, json);
    }

    /// <summary>
    /// Loads composite vectors from a JSON file.
    /// </summary>
    public static Dictionary<string, float[]> LoadFromJson(string jsonPath)
    {
        if (!File.Exists(jsonPath))
        {
            throw new FileNotFoundException($"JSON file not found: {jsonPath}", jsonPath);
        }

        var json = File.ReadAllText(jsonPath);
        return JsonSerializer.Deserialize<Dictionary<string, float[]>>(json)
            ?? throw new InvalidOperationException("Failed to deserialize composite vectors JSON.");
    }

    /// <summary>
    /// Computes cosine similarity between two composite vectors.
    /// Both vectors must be L2-normalized (which Combine() guarantees).
    /// </summary>
    public static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException("Vectors must have the same length");
        }

        float dot = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }

        return dot;
    }

    /// <summary>
    /// L2-normalizes a vector in place.
    /// </summary>
    private static void L2Normalize(Span<float> v)
    {
        float sumSq = 0;
        for (int i = 0; i < v.Length; i++)
        {
            sumSq += v[i] * v[i];
        }

        if (sumSq > 1e-12f)
        {
            float invNorm = 1f / MathF.Sqrt(sumSq);
            for (int i = 0; i < v.Length; i++)
            {
                v[i] *= invNorm;
            }
        }
    }
}
