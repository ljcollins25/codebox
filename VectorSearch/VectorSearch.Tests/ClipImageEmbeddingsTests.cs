#nullable disable
#nullable enable annotations
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using VectorSearch;
using Xunit;
using Xunit.Abstractions;

namespace VectorSearch.Tests;

using static Helpers;

/// <summary>
/// Tests for the Blast-Driven Hierarchical Graph Index.
/// </summary>
public class ClipImageEmbeddingsTests
{
    //public static string ModelPath = @"Q:\bin\vidtest\clip-vit-base-patch32.vision_model.onnx";
    public static string ModelPath = @"Q:\bin\vidtest\clip-vit-base-patch32.vision_model.onnx";
    public static string ModelName => Path.GetFileNameWithoutExtension(ModelPath);

    private readonly ITestOutputHelper _output;

    public ClipImageEmbeddingsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Tests the BlastIndex with a synthetic random dataset.
    /// </summary>
    [Theory]
    [InlineData(@"Q:\bin\vidtest\frames")]
    public void TestDataset(string imageFolderPath)
    {
        var outputFileName = Path.TrimEndingDirectorySeparator(imageFolderPath) + $".{ModelName}.json";
        ClipImageEmbeddings.GenerateImageEmbeddingsToJson(
            modelPath: ModelPath,
            inputFolderPath: imageFolderPath,
            outputJsonPath: outputFileName);
    }

    /// <summary>
    /// Tests getting embeddings for a single file and finding best matches using brute force.
    /// </summary>
    [Theory]
    [InlineData(@"Q:\bin\vidtest\frames.json", @"Q:\bin\vidtest\query.webp", 10)]
    [InlineData(@"Q:\bin\vidtest\frames.json", @"Q:\bin\vidtest\query.webp", 20)]
    [InlineData(@"Q:\bin\vidtest\frames.json", @"Q:\bin\vidtest\mlsleep.webp", 20)]
    [InlineData(@"Q:\bin\vidtest\frames.json", @"Q:\bin\vidtest\006448.jpg", 10)]
    public void TestSingleFileEmbeddingAndBruteForceSearch(
        string databaseJsonPath,
        string queryImagePath,
        int topK)
    {
        // Load database embeddings
        _output.WriteLine($"Loading database embeddings from: {databaseJsonPath}");
        var databaseEmbeddings = ClipImageEmbeddings.LoadEmbeddingsFromJson(databaseJsonPath);
        _output.WriteLine($"Loaded {databaseEmbeddings.Count} embeddings from database");

        // Get embeddings for query image
        _output.WriteLine($"Generating embeddings for query image: {queryImagePath}");
        var queryEmbeddings = ClipImageEmbeddings.GetEmbeddingsForFile(
            modelPath: ModelPath,
            imagePath: queryImagePath,
            normalize: true);
        _output.WriteLine($"Generated {queryEmbeddings.Count} crop embeddings for query");

        // Find best matching individual embeddings
        _output.WriteLine($"\n=== Top {topK} Best Matching Embeddings ===");
        var bestMatches = ClipImageEmbeddings.FindBestMatches(
            queryEmbeddings,
            databaseEmbeddings,
            topK);

        foreach (var (key, similarity) in bestMatches)
        {
            _output.WriteLine($"  {similarity:F4}  {key}");
        }

        // Find best matching frames (aggregated by filename)
        _output.WriteLine($"\n=== Top {topK} Best Matching Frames ===");
        var bestFrames = ClipImageEmbeddings.FindBestMatchingFrames(
            queryEmbeddings,
            databaseEmbeddings,
            topK);

        foreach (var (fileName, similarity) in bestFrames)
        {
            _output.WriteLine($"  {similarity:F4}  {fileName}");
        }

        // Assertions
        Assert.NotEmpty(bestMatches);
        Assert.NotEmpty(bestFrames);
        Assert.True(bestMatches[0].Similarity >= bestMatches[^1].Similarity, "Results should be sorted descending");
        Assert.True(bestFrames[0].Similarity >= bestFrames[^1].Similarity, "Results should be sorted descending");

        // Generate concatenated result image
        var queryDir = Path.GetDirectoryName(queryImagePath)!;
        var queryName = Path.GetFileNameWithoutExtension(queryImagePath);
        var resultImagePath = Path.Combine(queryDir, $"{queryName}.{ModelName}.{topK}.jpg");

        // Determine the database folder from the JSON path
        var databaseFolderPath = Path.ChangeExtension(databaseJsonPath, null);

        GenerateConcatenatedResultImage(
            queryImagePath,
            databaseFolderPath,
            bestFrames,
            resultImagePath);

        _output.WriteLine($"\nResult image saved to: {resultImagePath}");
    }

    /// <summary>
    /// Generates a concatenated image showing the query and top matching frames.
    /// </summary>
    private static void GenerateConcatenatedResultImage(
        string queryImagePath,
        string databaseFolderPath,
        List<(string FileName, float Similarity)> bestFrames,
        string outputPath)
    {
        const int thumbSize = 224;
        const int padding = 4;
        const int labelHeight = 20;

        // Load query image
        using var queryImage = Image.Load<Rgb24>(queryImagePath);

        // Calculate layout: query on left, results in a grid on right
        var resultCount = bestFrames.Count;
        var cols = (int)Math.Ceiling(Math.Sqrt(resultCount));
        var rows = (int)Math.Ceiling((double)resultCount / cols);

        var queryThumbWidth = thumbSize;
        var queryThumbHeight = thumbSize + labelHeight;
        var resultsWidth = cols * (thumbSize + padding) - padding;
        var resultsHeight = rows * (thumbSize + labelHeight + padding) - padding;

        var totalWidth = queryThumbWidth + padding * 2 + resultsWidth;
        var totalHeight = Math.Max(queryThumbHeight, resultsHeight);

        using var result = new Image<Rgb24>(totalWidth, totalHeight, new Rgb24(32, 32, 32));

        // Draw query image (resized to thumb size)
        using (var queryThumb = queryImage.Clone(ctx => ctx.Resize(thumbSize, thumbSize)))
        {
            result.Mutate(ctx => ctx.DrawImage(queryThumb, new Point(0, labelHeight), 1f));
        }

        // Draw result frames in a grid
        var startX = queryThumbWidth + padding * 2;
        for (int i = 0; i < bestFrames.Count; i++)
        {
            var (fileName, similarity) = bestFrames[i];
            var framePath = Path.Combine(databaseFolderPath, fileName);

            if (!File.Exists(framePath))
            {
                continue;
            }

            var col = i % cols;
            var row = i / cols;
            var x = startX + col * (thumbSize + padding);
            var y = row * (thumbSize + labelHeight + padding) + labelHeight;

            using var frameImage = Image.Load<Rgb24>(framePath);
            using var frameThumb = frameImage.Clone(ctx => ctx.Resize(thumbSize, thumbSize));
            result.Mutate(ctx => ctx.DrawImage(frameThumb, new Point(x, y), 1f));
        }

        // Save result
        result.SaveAsJpeg(outputPath);
    }

    /// <summary>
    /// Tests using an existing frame from the database as a query (should find itself as best match).
    /// </summary>
    [Theory]
    [InlineData(@"Q:\bin\vidtest\frames")]
    public void TestSelfMatchShouldBeHighestSimilarity(string databaseFolderPath)
    {
        var databaseJsonPath = Path.TrimEndingDirectorySeparator(databaseFolderPath) + ".json";

        // Ensure database exists
        if (!File.Exists(databaseJsonPath))
        {
            ClipImageEmbeddings.GenerateImageEmbeddingsToJson(
                modelPath: ModelPath,
                inputFolderPath: databaseFolderPath,
                outputJsonPath: databaseJsonPath);
        }

        // Load database
        var databaseEmbeddings = ClipImageEmbeddings.LoadEmbeddingsFromJson(databaseJsonPath);

        // Pick a random image from the database folder as query
        var imageFiles = Directory.GetFiles(databaseFolderPath, "*.jpg")
            .Concat(Directory.GetFiles(databaseFolderPath, "*.png"))
            .ToArray();

        if (imageFiles.Length == 0)
        {
            _output.WriteLine("No image files found in database folder, skipping test");
            return;
        }

        var queryImagePath = imageFiles[imageFiles.Length / 2]; // Pick middle image
        var queryFileName = Path.GetFileName(queryImagePath);
        _output.WriteLine($"Using query image: {queryFileName}");

        // Get embeddings for query
        var queryEmbeddings = ClipImageEmbeddings.GetEmbeddingsForFile(
            modelPath: ModelPath,
            imagePath: queryImagePath,
            normalize: true);

        // Find best matching frames
        var bestFrames = ClipImageEmbeddings.FindBestMatchingFrames(
            queryEmbeddings,
            databaseEmbeddings,
            topK: 5);

        _output.WriteLine("\nTop 5 matches:");
        foreach (var (fileName, similarity) in bestFrames)
        {
            _output.WriteLine($"  {similarity:F4}  {fileName}");
        }

        // The query image should match itself with very high similarity (close to 1.0)
        var selfMatch = bestFrames.FirstOrDefault(f => f.FileName == queryFileName);
        Assert.True(selfMatch.Similarity > 0.99f, 
            $"Self-match similarity should be > 0.99, got {selfMatch.Similarity}");
        Assert.Equal(queryFileName, bestFrames[0].FileName);
    }
}