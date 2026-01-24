#nullable disable
#nullable enable annotations
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
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
    //public static string ModelPath = @$"{Root}\clip-vit-base-patch32.vision_model.onnx";
    public static string ModelPath = @$"{Root}\{ModelName}.onnx";
    //public const string ModelName = "dinov2-small";
    //public const string ModelName = "clip-vit-base-patch32.vision_model";
    public const string ModelName = "clip-vit-base-patch32.vision_model_q4f16";

    public const string Root = @"Q:\bin\vidtest";

    private readonly ITestOutputHelper _output;

    public ClipImageEmbeddingsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Tests the BlastIndex with a synthetic random dataset.
    /// </summary>
    [Theory]
    [InlineData(@$"{Root}\frames")]
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
    [InlineData("frames", "query.webp", 10)]
    [InlineData("frames", "query.webp", 20)]
    [InlineData("frames", "mlsleep.webp", 20)]
    [InlineData("frames", "flbottle.webp", 20)]
    [InlineData("frames", "fvsit.webp", 20)]
    [InlineData("frames", "flbottle.webp", 5)]
    [InlineData("frames", "006448.jpg", 10)]
    public void TestSingleFileEmbeddingAndBruteForceSearch(
        string databaseName,
        string queryImageName,
        int topK)
    {
        var databaseJsonPath = Path.Combine(Root,  databaseName + $".{ModelName}.json");
        var queryImagePath = Path.Combine(Root, queryImageName);
        var databaseFolderPath = Path.Combine(Root, databaseName);

        // Load database embeddings
        _output.WriteLine($"Loading database embeddings from: {databaseJsonPath}");
        var databaseEmbeddings = ClipImageEmbeddings.LoadEmbeddingsFromJson(databaseJsonPath);
        _output.WriteLine($"Loaded {databaseEmbeddings.Count} embeddings from database");

        // Get embeddings for query image
        _output.WriteLine($"Generating embeddings for query image: {queryImagePath}");
        var queryEmbeddings = ClipImageEmbeddings.GetEmbeddingsForFile(
            modelPath: ModelPath,
            imagePath: queryImagePath,
            normalize: true,
            includeFlip: true);
        _output.WriteLine($"Generated {queryEmbeddings.Count} crop embeddings for query (including flipped)");

        // Find best matching individual embeddings
        _output.WriteLine($"\n=== Top {topK} Best Matching Embeddings ===");
        var bestMatches = ClipImageEmbeddings.FindBestMatches(
            queryEmbeddings,
            databaseEmbeddings,
            topK);

        int i = 0;
        foreach (var (key, similarity) in bestMatches)
        {
            _output.WriteLine($"{i++}  {similarity:F4}  {key}");
        }

        // Find best matching frames (aggregated by filename)
        _output.WriteLine($"\n=== Top {topK} Best Matching Frames ===");
        var bestFrames = ClipImageEmbeddings.FindBestMatchingFrames(
            queryEmbeddings,
            databaseEmbeddings,
            topK);
        i = 0;
        foreach (var (fileName, similarity) in bestFrames)
        {
            _output.WriteLine($"{i++}  {similarity:F4}  {fileName}");
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
        const int labelHeight = 18;

        // Load font for labels
        var fontFamily = SystemFonts.Get("Arial");
        var font = fontFamily.CreateFont(12, FontStyle.Regular);
        var textColor = Color.White;

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

        // Draw query label and image
        var queryFileName = Path.GetFileName(queryImagePath);
        result.Mutate(ctx => ctx.DrawText($"Q: {queryFileName}", font, textColor, new PointF(2, 2)));

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
            var y = row * (thumbSize + labelHeight + padding);

            // Draw label with rank, similarity, and filename
            var label = $"#{i + 1} {similarity:F3} {fileName}";
            result.Mutate(ctx => ctx.DrawText(label, font, textColor, new PointF(x + 2, y + 2)));

            // Draw thumbnail
            using var frameImage = Image.Load<Rgb24>(framePath);
            using var frameThumb = frameImage.Clone(ctx => ctx.Resize(thumbSize, thumbSize));
            result.Mutate(ctx => ctx.DrawImage(frameThumb, new Point(x, y + labelHeight), 1f));
        }

        // Save result
        result.SaveAsJpeg(outputPath);
    }

    /// <summary>
    /// Tests using an existing frame from the database as a query (should find itself as best match).
    /// </summary>
    [Theory]
    [InlineData(@$"{Root}\frames")]
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

    /// <summary>
    /// Tests ORB-based visual verification as a second stage after embedding search.
    /// </summary>
    [Theory]
    [InlineData("frames", "query.webp", 10)]
    [InlineData("frames", "query.webp", 20)]
    [InlineData("frames", "mlsleep.webp", 20)]
    [InlineData("frames", "flbottle.webp", 20)]
    [InlineData("frames", "fvsit.webp", 20)]
    [InlineData("frames", "flbottle.webp", 5)]
    [InlineData("frames", "006448.jpg", 10)]
    public void TestOrbVisualVerification(
        string databaseName,
        string queryImageName,
        int topK)
    {
        var databaseJsonPath = Path.Combine(Root, databaseName + $".{ModelName}.json");
        var queryImagePath = Path.Combine(Root, queryImageName);
        var databaseFolderPath = Path.Combine(Root, databaseName);

        // Load database embeddings
        _output.WriteLine($"Loading database embeddings from: {databaseJsonPath}");
        var databaseEmbeddings = ClipImageEmbeddings.LoadEmbeddingsFromJson(databaseJsonPath);
        _output.WriteLine($"Loaded {databaseEmbeddings.Count} embeddings from database");

        // Get embeddings for query image (with flip for better recall)
        _output.WriteLine($"Generating embeddings for query image: {queryImagePath}");
        var queryEmbeddings = ClipImageEmbeddings.GetEmbeddingsForFile(
            modelPath: ModelPath,
            imagePath: queryImagePath,
            normalize: true,
            includeFlip: true);

        // Stage 1: Find top K candidates using embedding similarity
        var modelSuffix = $".{ModelName}";
        if (databaseFolderPath.EndsWith(modelSuffix))
        {
            databaseFolderPath = databaseFolderPath[..^modelSuffix.Length];
        }

        var bestFrames = ClipImageEmbeddings.FindBestMatchingFrames(
            queryEmbeddings,
            databaseEmbeddings,
            topK);

        _output.WriteLine($"\n=== Stage 1: Top {topK} Embedding Matches ===");
        foreach (var (fileName, similarity) in bestFrames)
        {
            _output.WriteLine($"  {similarity:F4}  {fileName}");
        }

        // Stage 2: Rerank using ORB visual verification
        _output.WriteLine($"\n=== Stage 2: ORB Visual Verification ===");
        var candidatePaths = bestFrames
            .Select(f => Path.Combine(databaseFolderPath, f.FileName))
            .ToList();

        using var orbMatcher = new OrbVisualMatcher(nFeatures: 2000);
        var orbResults = orbMatcher.RankCandidates(queryImagePath, candidatePaths);

        _output.WriteLine($"\nORB Reranked Results:");
        foreach (var result in orbResults)
        {
            var fileName = Path.GetFileName(result.CandidatePath);
            var flipIndicator = result.WasFlippedMatch ? " [FLIPPED]" : "";
            _output.WriteLine($"  Inliers: {result.InlierCount,3} / {result.TotalGoodMatches,3} good matches  {fileName}{flipIndicator}");
        }

        // Get the best ORB match
        var bestOrbMatch = orbResults.FirstOrDefault();
        if (bestOrbMatch.InlierCount >= 8)
        {
            _output.WriteLine($"\nBest ORB match: {Path.GetFileName(bestOrbMatch.CandidatePath)} with {bestOrbMatch.InlierCount} inliers");
        }
        else
        {
            _output.WriteLine($"\nNo strong ORB match found (best had only {bestOrbMatch.InlierCount} inliers)");
        }

        // Generate result visualization
        var queryDir = Path.GetDirectoryName(queryImagePath)!;
        var queryName = Path.GetFileNameWithoutExtension(queryImagePath);
        var resultImagePath = Path.Combine(queryDir, $"{queryName}.{ModelName}.orb.{topK}.jpg");

        GenerateOrbResultImage(
            queryImagePath,
            orbResults,
            resultImagePath);

        _output.WriteLine($"\nResult image saved to: {resultImagePath}");
    }

    /// <summary>
    /// Generates a visualization of ORB matching results.
    /// </summary>
    private void GenerateOrbResultImage(
        string queryImagePath,
        List<OrbVisualMatcher.MatchResult> orbResults,
        string outputPath)
    {
        const int thumbSize = 224;
        const int padding = 4;
        const int labelHeight = 18;

        var fontFamily = SystemFonts.Get("Arial");
        var font = fontFamily.CreateFont(11, FontStyle.Regular);
        var textColor = Color.White;
        var highlightColor = Color.Lime;

        using var queryImage = Image.Load<Rgb24>(queryImagePath);

        var resultCount = orbResults.Count;
        var cols = (int)Math.Ceiling(Math.Sqrt(resultCount));
        var rows = (int)Math.Ceiling((double)resultCount / cols);

        var queryThumbWidth = thumbSize;
        var resultsWidth = cols * (thumbSize + padding) - padding;
        var resultsHeight = rows * (thumbSize + labelHeight + padding) - padding;

        var totalWidth = queryThumbWidth + padding * 2 + resultsWidth;
        var totalHeight = Math.Max(thumbSize + labelHeight, resultsHeight);

        using var result = new Image<Rgb24>(totalWidth, totalHeight, new Rgb24(32, 32, 32));

        // Draw query
        var queryFileName = Path.GetFileName(queryImagePath);
        result.Mutate(ctx => ctx.DrawText($"Q: {queryFileName}", font, textColor, new PointF(2, 2)));
        using (var queryThumb = queryImage.Clone(ctx => ctx.Resize(thumbSize, thumbSize)))
        {
            result.Mutate(ctx => ctx.DrawImage(queryThumb, new Point(0, labelHeight), 1f));
        }

        // Draw ORB results
        var startX = queryThumbWidth + padding * 2;
        for (int i = 0; i < orbResults.Count; i++)
        {
            var orbResult = orbResults[i];
            if (!File.Exists(orbResult.CandidatePath))
                continue;

            var col = i % cols;
            var row = i / cols;
            var x = startX + col * (thumbSize + padding);
            var y = row * (thumbSize + labelHeight + padding);

            // Label with rank, inlier count, and filename
            var fileName = Path.GetFileName(orbResult.CandidatePath);
            var flipMark = orbResult.WasFlippedMatch ? "F" : "";
            var label = $"#{i + 1} {orbResult.InlierCount}/{orbResult.TotalGoodMatches}{flipMark} {fileName}";
            var labelColor = orbResult.InlierCount >= 8 ? highlightColor : textColor;
            result.Mutate(ctx => ctx.DrawText(label, font, labelColor, new PointF(x + 2, y + 2)));

            using var frameImage = Image.Load<Rgb24>(orbResult.CandidatePath);
            using var frameThumb = frameImage.Clone(ctx => ctx.Resize(thumbSize, thumbSize));
            result.Mutate(ctx => ctx.DrawImage(frameThumb, new Point(x, y + labelHeight), 1f));
        }

        result.SaveAsJpeg(outputPath);
    }

    /// <summary>
    /// Tests BlastIndex search against brute force, using real image embeddings.
    /// </summary>
    [Theory]
    [InlineData("frames", "query.webp", 10)]
    [InlineData("frames", "mlsleep.webp", 10)]
    [InlineData("frames", "flbottle.webp", 10)]
    public void TestBlastIndexVsBruteForce(
        string databaseName,
        string queryImageName,
        int topK)
    {
        var databaseJsonPath = Path.Combine(Root, databaseName + $".{ModelName}.json");
        var queryImagePath = Path.Combine(Root, queryImageName);

        // Load database embeddings
        _output.WriteLine($"Loading database embeddings from: {databaseJsonPath}");
        var databaseEmbeddings = ClipImageEmbeddings.LoadEmbeddingsFromJson(databaseJsonPath);
        _output.WriteLine($"Loaded {databaseEmbeddings.Count} embeddings from database");

        // Convert to VectorBlockArrayStore for index
        var embeddingList = databaseEmbeddings.ToList();
        var dimension = embeddingList[0].Value.Length;
        var vectors = new float[embeddingList.Count, dimension];
        var keyToIndex = new Dictionary<string, int>();
        var indexToKey = new string[embeddingList.Count];

        for (int i = 0; i < embeddingList.Count; i++)
        {
            var (key, embedding) = embeddingList[i];
            keyToIndex[key] = i;
            indexToKey[i] = key;
            for (int j = 0; j < dimension; j++)
            {
                vectors[i, j] = embedding[j];
            }
        }

        var store = new VectorBlockArrayStore(vectors);
        var metric = new CosineFloatArrayMetric(dimension);

        // Build BlastIndex
        _output.WriteLine($"Building BlastIndex with {embeddingList.Count} vectors...");
        var index = new BlastIndex(metric, store, bucketCapacity: 64, outgoingNeighborCount: 16);
        for (int i = 0; i < embeddingList.Count; i++)
        {
            index.Insert(new VectorId(i));
        }
        _output.WriteLine("BlastIndex built.");

        // Get embeddings for query image
        var queryEmbeddings = ClipImageEmbeddings.GetEmbeddingsForFile(
            modelPath: ModelPath,
            imagePath: queryImagePath,
            normalize: true,
            includeFlip: true);

        // Use "center" crop as the query vector
        var queryVector = queryEmbeddings["center"];

        // Brute force search
        var bruteForceResults = new List<(string Key, float Distance)>();
        for (int i = 0; i < embeddingList.Count; i++)
        {
            var dist = metric.Distance(queryVector, store.GetVector(new VectorId(i)));
            bruteForceResults.Add((indexToKey[i], dist));
        }
        bruteForceResults.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var bruteForceTopK = bruteForceResults.Take(topK).ToList();

        // BlastIndex search
        var indexResults = index.Query(queryVector, topK);
        var indexTopK = indexResults.Select(r => (Key: indexToKey[r.Id], r.Distance)).ToList();

        // Output results
        _output.WriteLine($"\n=== Brute Force Top {topK} ===");
        for (int i = 0; i < bruteForceTopK.Count; i++)
        {
            var (key, dist) = bruteForceTopK[i];
            _output.WriteLine($"  {i + 1}. {dist:F6}  {key}");
        }

        _output.WriteLine($"\n=== BlastIndex Top {topK} ===");
        for (int i = 0; i < indexTopK.Count; i++)
        {
            var (key, dist) = indexTopK[i];
            _output.WriteLine($"  {i + 1}. {dist:F6}  {key}");
        }

        // Calculate recall
        var bruteForceSet = bruteForceTopK.Select(x => x.Key).ToHashSet();
        var indexSet = indexTopK.Select(x => x.Key).ToHashSet();
        var intersection = bruteForceSet.Intersect(indexSet).Count();
        var recall = (float)intersection / topK;
        _output.WriteLine($"\nRecall@{topK}: {recall:P1} ({intersection}/{topK})");

        // Verify top-1 match
        Assert.Equal(bruteForceTopK[0].Key, indexTopK[0].Key);
        Assert.True(recall >= 0.7f, $"Recall should be at least 70%, got {recall:P1}");
    }

    /// <summary>
    /// Tests IHCITree search against brute force, using real image embeddings.
    /// </summary>
    [Theory]
    [InlineData("frames", "query.webp", 10)]
    [InlineData("frames", "mlsleep.webp", 10)]
    [InlineData("frames", "flbottle.webp", 10)]
    public void TestIHCITreeVsBruteForce(
        string databaseName,
        string queryImageName,
        int topK)
    {
        var databaseJsonPath = Path.Combine(Root, databaseName + $".{ModelName}.json");
        var queryImagePath = Path.Combine(Root, queryImageName);

        // Load database embeddings
        _output.WriteLine($"Loading database embeddings from: {databaseJsonPath}");
        var databaseEmbeddings = ClipImageEmbeddings.LoadEmbeddingsFromJson(databaseJsonPath);
        _output.WriteLine($"Loaded {databaseEmbeddings.Count} embeddings from database");

        // Convert to VectorBlockArrayStore for index
        var embeddingList = databaseEmbeddings.ToList();
        var dimension = embeddingList[0].Value.Length;
        var vectors = new float[embeddingList.Count, dimension];
        var keyToIndex = new Dictionary<string, int>();
        var indexToKey = new string[embeddingList.Count];

        for (int i = 0; i < embeddingList.Count; i++)
        {
            var (key, embedding) = embeddingList[i];
            keyToIndex[key] = i;
            indexToKey[i] = key;
            for (int j = 0; j < dimension; j++)
            {
                vectors[i, j] = embedding[j];
            }
        }

        var store = new VectorBlockArrayStore(vectors);
        var metric = new CosineFloatArrayMetric(dimension);

        // Build IHCITree
        _output.WriteLine($"Building IHCITree with {embeddingList.Count} vectors...");
        var tree = new IHCITree(metric, store, leafCapacity: 64, routingMaxChildren: 16, leafNeighborCount: 8);
        for (int i = 0; i < embeddingList.Count; i++)
        {
            tree.Insert(new VectorId(i));
        }
        _output.WriteLine("IHCITree built.");

        // Get embeddings for query image
        var queryEmbeddings = ClipImageEmbeddings.GetEmbeddingsForFile(
            modelPath: ModelPath,
            imagePath: queryImagePath,
            normalize: true,
            includeFlip: true);

        // Use "center" crop as the query vector
        var queryVector = queryEmbeddings["center"];

        // Brute force search
        var bruteForceResults = new List<(string Key, float Distance)>();
        for (int i = 0; i < embeddingList.Count; i++)
        {
            var dist = metric.Distance(queryVector, store.GetVector(new VectorId(i)));
            bruteForceResults.Add((indexToKey[i], dist));
        }
        bruteForceResults.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var bruteForceTopK = bruteForceResults.Take(topK).ToList();

        // IHCITree search
        var treeResults = tree.Query(queryVector, topK, routingWidth: 4);
        var treeTopK = treeResults.Select(r => (Key: indexToKey[r.Id], r.Distance)).ToList();

        // Output results
        _output.WriteLine($"\n=== Brute Force Top {topK} ===");
        for (int i = 0; i < bruteForceTopK.Count; i++)
        {
            var (key, dist) = bruteForceTopK[i];
            _output.WriteLine($"  {i + 1}. {dist:F6}  {key}");
        }

        _output.WriteLine($"\n=== IHCITree Top {topK} ===");
        for (int i = 0; i < treeTopK.Count; i++)
        {
            var (key, dist) = treeTopK[i];
            _output.WriteLine($"  {i + 1}. {dist:F6}  {key}");
        }

        // Calculate recall
        var bruteForceSet = bruteForceTopK.Select(x => x.Key).ToHashSet();
        var treeSet = treeTopK.Select(x => x.Key).ToHashSet();
        var intersection = bruteForceSet.Intersect(treeSet).Count();
        var recall = (float)intersection / topK;
        _output.WriteLine($"\nRecall@{topK}: {recall:P1} ({intersection}/{topK})");

        // Verify top-1 match
        Assert.Equal(bruteForceTopK[0].Key, treeTopK[0].Key);
        Assert.True(recall >= 0.7f, $"Recall should be at least 70%, got {recall:P1}");
    }
}