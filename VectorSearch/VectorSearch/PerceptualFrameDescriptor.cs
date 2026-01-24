using System.Text.Json;
using OpenCvSharp;

namespace VectorSearch;

/// <summary>
/// Computes a fixed-length perceptual frame vector for image similarity comparison.
/// Designed to be robust to partial crops, UI overlays, and horizontal mirroring.
/// 
/// Components:
/// - Color distribution (CIELab histogram): 32 dims
/// - Luminance statistics: 6 dims
/// - Texture statistics (uniform LBP): 59 dims
/// - Edge orientation distribution: 16 dims
/// Total: 113 dimensions
/// </summary>
public static class PerceptualFrameDescriptor
{
    /// <summary>Total dimension of the perceptual frame vector.</summary>
    public const int Dimension = 32 + 6 + 59 + 16; // = 113

    private static readonly string[] SupportedExtensions = [".jpg", ".jpeg", ".png", ".webp", ".bmp"];

    // Histogram bin counts
    private const int LBins = 16;      // Luminance bins
    private const int ABins = 8;       // a* channel bins
    private const int BBins = 8;       // b* channel bins
    private const int LbpBins = 59;    // Uniform LBP patterns (P=8, R=1)
    private const int OrientationBins = 8;
    private const int MagnitudeBins = 8;

    // LBP uniform pattern lookup table (maps 0-255 to uniform pattern index or non-uniform bin)
    private static readonly int[] UniformLbpLut = BuildUniformLbpLut();

    /// <summary>
    /// Computes the perceptual frame descriptor for an image.
    /// </summary>
    /// <param name="imagePath">Path to the image file</param>
    /// <returns>L2-normalized perceptual vector of length 113</returns>
    public static float[] Compute(string imagePath)
    {
        using var image = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (image.Empty())
            throw new FileNotFoundException($"Could not load image: {imagePath}");

        return Compute(image);
    }

    /// <summary>
    /// Computes the perceptual frame descriptor for an image.
    /// </summary>
    /// <param name="image">BGR image (OpenCV format)</param>
    /// <returns>L2-normalized perceptual vector of length 113</returns>
    public static float[] Compute(Mat image)
    {
        if (image.Empty())
            throw new ArgumentException("Image is empty", nameof(image));

        var result = new float[Dimension];
        int offset = 0;

        // Convert to Lab color space
        using var lab = new Mat();
        Cv2.CvtColor(image, lab, ColorConversionCodes.BGR2Lab);

        // Convert to grayscale for luminance-based features
        using var gray = new Mat();
        Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);

        // Component 1: Color distribution (Lab histogram) - 32 dims
        ComputeLabHistogram(lab, result.AsSpan(offset, LBins + ABins + BBins));
        offset += LBins + ABins + BBins;

        // Component 2: Luminance statistics - 6 dims
        ComputeLuminanceStatistics(gray, result.AsSpan(offset, 6));
        offset += 6;

        // Component 3: Texture statistics (uniform LBP) - 59 dims
        ComputeLbpHistogram(gray, result.AsSpan(offset, LbpBins));
        offset += LbpBins;

        // Component 4: Edge orientation distribution - 16 dims
        ComputeEdgeHistograms(gray, result.AsSpan(offset, OrientationBins + MagnitudeBins));
        offset += OrientationBins + MagnitudeBins;

        // Final L2 normalization of the entire vector
        L2Normalize(result);

        return result;
    }

    /// <summary>
    /// Generates perceptual frame descriptors for all images in a folder and saves to JSON.
    /// </summary>
    /// <param name="inputFolderPath">Folder containing image files</param>
    /// <param name="outputJsonPath">Path to the output JSON file</param>
    public static void GenerateDescriptorsToJson(string inputFolderPath, string outputJsonPath)
    {
        if (!Directory.Exists(inputFolderPath))
        {
            throw new DirectoryNotFoundException($"Input folder does not exist: {inputFolderPath}");
        }

        // Find all valid image files
        var imageFiles = Directory.EnumerateFiles(inputFolderPath)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        if (imageFiles.Count == 0)
        {
            throw new InvalidOperationException($"No valid image files found in: {inputFolderPath}");
        }

        var results = new Dictionary<string, float[]>(imageFiles.Count);

        // Process images (can be parallelized since descriptors are independent)
        var lockObj = new object();
        Parallel.ForEach(imageFiles, imagePath =>
        {
            var fileName = Path.GetFileName(imagePath);
            var descriptor = Compute(imagePath);

            lock (lockObj)
            {
                results[fileName] = descriptor;
            }
        });

        // Serialize and save
        var options = new JsonSerializerOptions { WriteIndented = false };
        var json = JsonSerializer.Serialize(results, options);
        File.WriteAllText(outputJsonPath, json);
    }

    /// <summary>
    /// Loads perceptual descriptors from a JSON file.
    /// </summary>
    /// <param name="jsonPath">Path to the JSON file</param>
    /// <returns>Dictionary mapping filename to descriptor vector</returns>
    public static Dictionary<string, float[]> LoadDescriptorsFromJson(string jsonPath)
    {
        if (!File.Exists(jsonPath))
        {
            throw new FileNotFoundException($"JSON file not found: {jsonPath}", jsonPath);
        }

        var json = File.ReadAllText(jsonPath);
        return JsonSerializer.Deserialize<Dictionary<string, float[]>>(json)
            ?? throw new InvalidOperationException("Failed to deserialize descriptors JSON.");
    }

    /// <summary>
    /// Computes Lab color histogram (L: 16 bins, a: 8 bins, b: 8 bins).
    /// Invariant to spatial position, cropping, and mirroring.
    /// </summary>
    private static void ComputeLabHistogram(Mat lab, Span<float> output)
    {
        // Split Lab channels
        using var channels = new Mat();
        Cv2.Split(lab, out Mat[] labChannels);

        try
        {
            // L channel histogram (0-255 in OpenCV Lab, but typically 0-100 scaled)
            using var lHist = new Mat();
            Cv2.CalcHist([labChannels[0]], [0], null, lHist, 1, [LBins], [new Rangef(0, 256)]);
            CopyHistogramToSpan(lHist, output[..LBins]);

            // a* channel histogram (0-255 in OpenCV, centered at 128)
            using var aHist = new Mat();
            Cv2.CalcHist([labChannels[1]], [0], null, aHist, 1, [ABins], [new Rangef(0, 256)]);
            CopyHistogramToSpan(aHist, output.Slice(LBins, ABins));

            // b* channel histogram (0-255 in OpenCV, centered at 128)
            using var bHist = new Mat();
            Cv2.CalcHist([labChannels[2]], [0], null, bHist, 1, [BBins], [new Rangef(0, 256)]);
            CopyHistogramToSpan(bHist, output.Slice(LBins + ABins, BBins));
        }
        finally
        {
            foreach (var ch in labChannels) ch.Dispose();
        }

        // L2 normalize the color histogram component
        L2Normalize(output);
    }

    /// <summary>
    /// Computes luminance statistics: mean, std, skewness, kurtosis, 5th/95th percentiles.
    /// Provides global brightness and contrast characteristics.
    /// </summary>
    private static void ComputeLuminanceStatistics(Mat gray, Span<float> output)
    {
        // Get all pixel values
        gray.GetArray(out byte[] pixels);
        int n = pixels.Length;

        if (n == 0)
        {
            output.Clear();
            return;
        }

        // Compute mean
        double sum = 0;
        for (int i = 0; i < n; i++)
            sum += pixels[i];
        double mean = sum / n;

        // Compute variance, skewness, kurtosis in a single pass
        double m2 = 0, m3 = 0, m4 = 0;
        for (int i = 0; i < n; i++)
        {
            double diff = pixels[i] - mean;
            double diff2 = diff * diff;
            m2 += diff2;
            m3 += diff2 * diff;
            m4 += diff2 * diff2;
        }

        double variance = m2 / n;
        double std = Math.Sqrt(variance);

        // Skewness: E[(X-μ)³] / σ³
        double skewness = std > 1e-6 ? (m3 / n) / (std * std * std) : 0;

        // Kurtosis: E[(X-μ)⁴] / σ⁴ - 3 (excess kurtosis)
        double kurtosis = std > 1e-6 ? (m4 / n) / (variance * variance) - 3 : 0;

        // Compute percentiles (sort a sample if image is large, or full array if small)
        Array.Sort(pixels);
        int idx5 = (int)(n * 0.05);
        int idx95 = (int)(n * 0.95);
        double p5 = pixels[idx5];
        double p95 = pixels[Math.Min(idx95, n - 1)];

        // Normalize to roughly [0,1] range before storing
        output[0] = (float)(mean / 255.0);           // Mean: [0,1]
        output[1] = (float)(std / 128.0);            // Std: typically [0,1]
        output[2] = (float)(skewness / 3.0 + 0.5);   // Skewness: roughly center around 0.5
        output[3] = (float)(kurtosis / 10.0 + 0.5);  // Kurtosis: roughly center around 0.5
        output[4] = (float)(p5 / 255.0);             // 5th percentile: [0,1]
        output[5] = (float)(p95 / 255.0);            // 95th percentile: [0,1]

        // L2 normalize this component
        L2Normalize(output);
    }

    /// <summary>
    /// Computes uniform Local Binary Pattern histogram (P=8, R=1).
    /// Captures texture information invariant to spatial position.
    /// </summary>
    private static void ComputeLbpHistogram(Mat gray, Span<float> output)
    {
        output.Clear();

        int rows = gray.Rows;
        int cols = gray.Cols;

        // Skip border pixels (R=1)
        var indexer = gray.GetGenericIndexer<byte>();

        for (int y = 1; y < rows - 1; y++)
        {
            for (int x = 1; x < cols - 1; x++)
            {
                byte center = indexer[y, x];

                // Compute 8-neighbor LBP pattern (clockwise from top-left)
                // Neighbors at radius R=1:
                //   7 0 1
                //   6 c 2
                //   5 4 3
                int pattern = 0;
                pattern |= (indexer[y - 1, x - 1] >= center ? 1 : 0) << 7;  // Top-left
                pattern |= (indexer[y - 1, x] >= center ? 1 : 0) << 0;      // Top
                pattern |= (indexer[y - 1, x + 1] >= center ? 1 : 0) << 1;  // Top-right
                pattern |= (indexer[y, x + 1] >= center ? 1 : 0) << 2;      // Right
                pattern |= (indexer[y + 1, x + 1] >= center ? 1 : 0) << 3;  // Bottom-right
                pattern |= (indexer[y + 1, x] >= center ? 1 : 0) << 4;      // Bottom
                pattern |= (indexer[y + 1, x - 1] >= center ? 1 : 0) << 5;  // Bottom-left
                pattern |= (indexer[y, x - 1] >= center ? 1 : 0) << 6;      // Left

                // Map to uniform pattern bin (0-57 for uniform, 58 for non-uniform)
                int bin = UniformLbpLut[pattern];
                output[bin]++;
            }
        }

        // Normalize the LBP histogram
        L2Normalize(output);
    }

    /// <summary>
    /// Computes edge orientation and magnitude histograms using Sobel gradients.
    /// Uses unsigned orientation (0-180°) for mirror invariance.
    /// </summary>
    private static void ComputeEdgeHistograms(Mat gray, Span<float> output)
    {
        output.Clear();

        var orientationHist = output[..OrientationBins];
        var magnitudeHist = output.Slice(OrientationBins, MagnitudeBins);

        // Compute Sobel gradients
        using var gradX = new Mat();
        using var gradY = new Mat();
        Cv2.Sobel(gray, gradX, MatType.CV_32F, 1, 0, ksize: 3);
        Cv2.Sobel(gray, gradY, MatType.CV_32F, 0, 1, ksize: 3);

        // Compute magnitude and orientation
        using var magnitude = new Mat();
        using var orientation = new Mat();
        Cv2.CartToPolar(gradX, gradY, magnitude, orientation, angleInDegrees: true);

        // Get data
        magnitude.GetArray(out float[] magData);
        orientation.GetArray(out float[] oriData);

        // Compute magnitude threshold (e.g., 10% of max gradient)
        float maxMag = 0;
        for (int i = 0; i < magData.Length; i++)
            if (magData[i] > maxMag) maxMag = magData[i];
        float magThreshold = maxMag * 0.1f;

        // Maximum expected magnitude for histogram binning
        float maxMagForHist = maxMag * 0.8f; // Cap at 80% to reduce outlier effect
        if (maxMagForHist < 1) maxMagForHist = 1;

        // Build histograms for pixels above threshold
        for (int i = 0; i < magData.Length; i++)
        {
            float mag = magData[i];
            if (mag < magThreshold) continue;

            // Unsigned orientation: map 0-360 to 0-180 for mirror invariance
            float ori = oriData[i];
            if (ori >= 180) ori -= 180;

            // Orientation histogram bin (0-180 degrees into 8 bins)
            int oriBin = Math.Min((int)(ori / 180f * OrientationBins), OrientationBins - 1);
            orientationHist[oriBin] += mag; // Weight by magnitude

            // Magnitude histogram bin
            int magBin = Math.Min((int)(mag / maxMagForHist * MagnitudeBins), MagnitudeBins - 1);
            magnitudeHist[magBin]++;
        }

        // Normalize orientation histogram
        L2Normalize(orientationHist);

        // Normalize magnitude histogram
        L2Normalize(magnitudeHist);
    }

    /// <summary>
    /// Builds lookup table mapping LBP patterns (0-255) to uniform pattern bins.
    /// Uniform patterns have at most 2 bitwise transitions (0→1 or 1→0).
    /// P=8, R=1: 58 uniform patterns + 1 non-uniform bin = 59 bins total.
    /// </summary>
    private static int[] BuildUniformLbpLut()
    {
        var lut = new int[256];
        int uniformIndex = 0;

        for (int i = 0; i < 256; i++)
        {
            // Count number of bitwise transitions (0→1 or 1→0) in circular pattern
            int transitions = CountTransitions(i);

            if (transitions <= 2)
            {
                // Uniform pattern - assign sequential index
                lut[i] = uniformIndex++;
            }
            else
            {
                // Non-uniform pattern - assign to last bin (58)
                lut[i] = LbpBins - 1;
            }
        }

        return lut;
    }

    /// <summary>
    /// Counts the number of 0↔1 transitions in an 8-bit circular pattern.
    /// </summary>
    private static int CountTransitions(int pattern)
    {
        int transitions = 0;
        int prev = (pattern >> 7) & 1; // Start with MSB

        for (int bit = 0; bit < 8; bit++)
        {
            int curr = (pattern >> bit) & 1;
            if (curr != prev) transitions++;
            prev = curr;
        }

        return transitions;
    }

    /// <summary>
    /// Copies histogram data from Mat to Span.
    /// </summary>
    private static void CopyHistogramToSpan(Mat hist, Span<float> output)
    {
        hist.GetArray(out float[] data);
        for (int i = 0; i < output.Length && i < data.Length; i++)
        {
            output[i] = data[i];
        }
    }

    /// <summary>
    /// L2-normalizes a span in place.
    /// </summary>
    private static void L2Normalize(Span<float> v)
    {
        float sumSq = 0;
        for (int i = 0; i < v.Length; i++)
            sumSq += v[i] * v[i];

        if (sumSq > 1e-12f)
        {
            float invNorm = 1f / MathF.Sqrt(sumSq);
            for (int i = 0; i < v.Length; i++)
                v[i] *= invNorm;
        }
    }

    /// <summary>
    /// Computes cosine similarity between two perceptual descriptors.
    /// </summary>
    public static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Vectors must have the same length");

        float dot = 0;
        for (int i = 0; i < a.Length; i++)
            dot += a[i] * b[i];

        return dot; // Both vectors are L2-normalized, so dot = cosine similarity
    }

    /// <summary>
    /// Computes L2 distance between two perceptual descriptors.
    /// </summary>
    public static float L2Distance(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Vectors must have the same length");

        float sumSq = 0;
        for (int i = 0; i < a.Length; i++)
        {
            float diff = a[i] - b[i];
            sumSq += diff * diff;
        }

        return MathF.Sqrt(sumSq);
    }
}
