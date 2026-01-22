using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace VectorSearch;

/// <summary>
/// Generates CLIP ViT-B/32 image embeddings for video frames using ONNX Runtime.
/// Produces five deterministic crops per image for robust matching.
/// </summary>
public static class ClipImageEmbeddings
{
    // CLIP ViT-B/32 normalization constants (MUST MATCH EXACTLY)
    private static readonly float[] ClipMean = [0.48145466f, 0.45782750f, 0.40821073f];
    private static readonly float[] ClipStd = [0.26862954f, 0.26130258f, 0.27577711f];

    // Model input/output tensor names
    private const string InputTensorName = "pixel_values";
    private const string OutputTensorName = "image_embeds";

    // Image dimensions
    private const int ImageSize = 224;
    private const int EmbeddingDimension = 512;

    // Crop names in fixed order
    private static readonly string[] CropNames = ["full", "center", "vertical", "top", "bottom"];

    // Supported image extensions
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png"
    };

    /// <summary>
    /// Generates image embeddings for all frames in a folder and writes them to a JSON file.
    /// </summary>
    /// <param name="inputFolderPath">Folder containing image files (.jpg, .jpeg, .png)</param>
    /// <param name="outputJsonPath">Path to the output JSON file</param>
    /// <param name="normalize">If true, apply L2 normalization to each embedding vector</param>
    /// <exception cref="DirectoryNotFoundException">Input folder does not exist</exception>
    /// <exception cref="InvalidOperationException">No valid image files found or model path not set</exception>
    /// <exception cref="FileNotFoundException">ONNX model file not found</exception>
    public static void GenerateImageEmbeddingsToJson(
        string modelPath,
        string inputFolderPath,
        string outputJsonPath,
        bool normalize = true)
    {
        // Validate inputs
        if (!Directory.Exists(inputFolderPath))
        {
            throw new DirectoryNotFoundException($"Input folder does not exist: {inputFolderPath}");
        }

        if (string.IsNullOrEmpty(modelPath))
        {
            throw new InvalidOperationException("ModelPath must be set before generating embeddings.");
        }

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"ONNX model file not found: {modelPath}", modelPath);
        }

        // Find all valid image files
        var imageFiles = Directory.EnumerateFiles(inputFolderPath)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        if (imageFiles.Count == 0)
        {
            throw new InvalidOperationException($"No valid image files found in: {inputFolderPath}");
        }


        var results = new KeyValuePair<string, float[]>[imageFiles.Count * CropNames.Length];
        // Create result dictionary: filename -> crop -> embedding

        // Load ONNX model once and reuse for all images
        using var session = new InferenceSession(modelPath);

        int processedImages = 0;

        Helpers.For(parallel: true, 0, imageFiles.Count,
            // Pre-allocate tensor for reuse
            localInit: () => new DenseTensor<float>([1, 3, ImageSize, ImageSize]),
            body: (index, state, inputTensor) =>
            {
                var imagePath = imageFiles[index];
                var fileName = Path.GetFileName(imagePath);
                var cropEmbeddings = new Dictionary<string, float[]>();

                // Load image once
                using var image = Image.Load<Rgb24>(imagePath);

                var offset = index * CropNames.Length;

                // Generate embedding for each crop
                foreach (var cropName in CropNames)
                {
                    // Get the cropped and resized image
                    using var croppedImage = ApplyCrop(image, cropName);

                    // Preprocess: convert to normalized tensor
                    PreprocessImage(croppedImage, inputTensor);

                    // Run inference
                    var embedding = RunInference(session, inputTensor);

                    // Apply L2 normalization if requested
                    if (normalize)
                    {
                        L2Normalize(embedding);
                    }

                    results[offset++] = new($"{fileName}/{cropName}", embedding);
                }

                Interlocked.Increment(ref processedImages);
                return inputTensor;
            },
            _ => { });

        // Write JSON output
        WriteJsonOutput(results.ToDictionary(e => e.Key, e => e.Value), outputJsonPath);
    }

    /// <summary>
    /// Applies the specified crop to an image and resizes to 224x224.
    /// </summary>
    private static Image<Rgb24> ApplyCrop(Image<Rgb24> source, string cropName)
    {
        var width = source.Width;
        var height = source.Height;

        Rectangle cropRect = cropName switch
        {
            "full" => new Rectangle(0, 0, width, height),
            "center" => GetCenterSquareCrop(width, height),
            "vertical" => GetVerticalCrop(width, height),
            "top" => GetTopCrop(width, height),
            "bottom" => GetBottomCrop(width, height),
            _ => throw new ArgumentException($"Unknown crop name: {cropName}")
        };

        // Clone, crop, and resize
        var cropped = source.Clone(ctx =>
        {
            ctx.Crop(cropRect);
            ctx.Resize(ImageSize, ImageSize);
        });

        return cropped;
    }

    /// <summary>
    /// Gets the largest centered square crop.
    /// </summary>
    private static Rectangle GetCenterSquareCrop(int width, int height)
    {
        var size = Math.Min(width, height);
        var x = (width - size) / 2;
        var y = (height - size) / 2;
        return new Rectangle(x, y, size, size);
    }

    /// <summary>
    /// Gets a centered 9:16 (portrait) crop.
    /// </summary>
    private static Rectangle GetVerticalCrop(int width, int height)
    {
        // Target aspect ratio is 9:16 (width:height)
        const float targetAspect = 9f / 16f;

        int cropWidth, cropHeight;
        float currentAspect = (float)width / height;

        if (currentAspect > targetAspect)
        {
            // Image is wider than target - crop width
            cropHeight = height;
            cropWidth = (int)(height * targetAspect);
        }
        else
        {
            // Image is taller or equal - crop height
            cropWidth = width;
            cropHeight = (int)(width / targetAspect);
        }

        // Center the crop
        var x = (width - cropWidth) / 2;
        var y = (height - cropHeight) / 2;

        return new Rectangle(x, y, cropWidth, cropHeight);
    }

    /// <summary>
    /// Gets a top-weighted crop (upper portion of image).
    /// Uses a square crop anchored at the top-center.
    /// </summary>
    private static Rectangle GetTopCrop(int width, int height)
    {
        var size = Math.Min(width, height);
        var x = (width - size) / 2;
        var y = 0; // Anchored at top
        return new Rectangle(x, y, size, size);
    }

    /// <summary>
    /// Gets a bottom-weighted crop (lower portion of image).
    /// Uses a square crop anchored at the bottom-center.
    /// </summary>
    private static Rectangle GetBottomCrop(int width, int height)
    {
        var size = Math.Min(width, height);
        var x = (width - size) / 2;
        var y = height - size; // Anchored at bottom
        return new Rectangle(x, y, size, size);
    }

    /// <summary>
    /// Preprocesses an image to a normalized NCHW tensor for CLIP inference.
    /// </summary>
    private static void PreprocessImage(Image<Rgb24> image, DenseTensor<float> tensor)
    {
        // Image must already be 224x224
        if (image.Width != ImageSize || image.Height != ImageSize)
        {
            throw new ArgumentException($"Image must be {ImageSize}x{ImageSize}");
        }

        // Process each pixel
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < ImageSize; y++)
            {
                var pixelRow = accessor.GetRowSpan(y);

                for (var x = 0; x < ImageSize; x++)
                {
                    var pixel = pixelRow[x];

                    // Convert to [0,1] range
                    var r = pixel.R / 255f;
                    var g = pixel.G / 255f;
                    var b = pixel.B / 255f;

                    // Apply CLIP normalization: (value - mean) / std
                    // Tensor layout: [N, C, H, W] = [1, 3, 224, 224]
                    tensor[0, 0, y, x] = (r - ClipMean[0]) / ClipStd[0]; // R channel
                    tensor[0, 1, y, x] = (g - ClipMean[1]) / ClipStd[1]; // G channel
                    tensor[0, 2, y, x] = (b - ClipMean[2]) / ClipStd[2]; // B channel
                }
            }
        });
    }

    /// <summary>
    /// Runs ONNX inference and returns the embedding vector.
    /// </summary>
    private static float[] RunInference(InferenceSession session, DenseTensor<float> inputTensor)
    {
        // Create input container
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(InputTensorName, inputTensor)
        };

        // Run inference
        using var results = session.Run(inputs);

        // Extract output tensor
        var outputTensor = results.First(r => r.Name == OutputTensorName).AsEnumerable<float>().ToArray();

        if (outputTensor.Length != EmbeddingDimension)
        {
            throw new InvalidOperationException(
                $"Expected embedding dimension {EmbeddingDimension}, got {outputTensor.Length}");
        }

        return outputTensor;
    }

    /// <summary>
    /// Applies L2 normalization to a vector in-place.
    /// </summary>
    private static void L2Normalize(float[] vector)
    {
        var sumSquares = 0f;
        for (var i = 0; i < vector.Length; i++)
        {
            sumSquares += vector[i] * vector[i];
        }

        var magnitude = MathF.Sqrt(sumSquares);

        if (magnitude > 0)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] /= magnitude;
            }
        }
    }

    /// <summary>
    /// Writes the embeddings dictionary to a JSON file.
    /// </summary>
    private static void WriteJsonOutput(
        IReadOnlyDictionary<string, float[]> results,
        string outputJsonPath)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = false // Compact output for production
        };

        var json = JsonSerializer.Serialize(results, options);

        // Ensure output directory exists
        var outputDir = Path.GetDirectoryName(outputJsonPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        File.WriteAllText(outputJsonPath, json);
    }
}
