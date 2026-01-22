#nullable disable
#nullable enable annotations
using VectorSearch;
using Xunit;

namespace VectorSearch.Tests;

using static Helpers;

/// <summary>
/// Tests for the Blast-Driven Hierarchical Graph Index.
/// </summary>
public class ClipImageEmbeddingsTests
{
    public static string ModelPath = @"Q:\bin\vidtest\vision_model.onnx";

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
}