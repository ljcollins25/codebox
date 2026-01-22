#nullable disable
#nullable enable annotations
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
    public static string ModelPath = @"Q:\bin\vidtest\vision_model.onnx";

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
        var outputFileName = Path.TrimEndingDirectorySeparator(imageFolderPath) + ".json";
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
    [InlineData(@"Q:\bin\vidtest\frames.json", @"Q:\bin\vidtest\frames\006448.jpg", 10)]
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