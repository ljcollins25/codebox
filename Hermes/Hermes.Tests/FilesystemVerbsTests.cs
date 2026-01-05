using System.Text.Json;
using System.Text.Json.Serialization;
using Hermes.Core;
using Hermes.Verbs.Filesystem;
using Xunit;

namespace Hermes.Tests;

public class FilesystemVerbsTests : IDisposable
{
    private readonly string _testDir;
    private readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public FilesystemVerbsTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "hermes_tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, recursive: true);
        }
    }

    [Fact]
    public void FsExists_ExistingFile_ReturnsTrue()
    {
        var testFile = Path.Combine(_testDir, "test.txt");
        File.WriteAllText(testFile, "content");

        var result = FilesystemHandlers.Exists(new FsExistsArgs { Path = testFile });

        Assert.True(result.Succeeded);
        Assert.True(result.Exists);
    }

    [Fact]
    public void FsExists_NonExistingFile_ReturnsFalse()
    {
        var result = FilesystemHandlers.Exists(new FsExistsArgs { Path = Path.Combine(_testDir, "nonexistent.txt") });

        Assert.True(result.Succeeded);
        Assert.False(result.Exists);
    }

    [Fact]
    public void FsReadFile_ExistingFile_ReturnsContent()
    {
        var testFile = Path.Combine(_testDir, "test.txt");
        File.WriteAllText(testFile, "hello world");

        var result = FilesystemHandlers.ReadFile(new FsReadFileArgs { Path = testFile });

        Assert.True(result.Succeeded);
        Assert.Equal("hello world", result.Content);
    }

    [Fact]
    public void FsReadFile_NonExistingFile_ReturnsFailed()
    {
        var result = FilesystemHandlers.ReadFile(new FsReadFileArgs { Path = Path.Combine(_testDir, "nonexistent.txt") });

        Assert.False(result.Succeeded);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void FsWriteFile_CreatesParentDirectories()
    {
        var testFile = Path.Combine(_testDir, "subdir", "nested", "test.txt");

        var result = FilesystemHandlers.WriteFile(new FsWriteFileArgs { Path = testFile, Content = "content" });

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(testFile));
        Assert.Equal("content", File.ReadAllText(testFile));
    }

    [Fact]
    public void FsReadRange_ReturnsCorrectLines()
    {
        var testFile = Path.Combine(_testDir, "test.txt");
        File.WriteAllLines(testFile, ["line1", "line2", "line3", "line4", "line5"]);

        var result = FilesystemHandlers.ReadRange(new FsReadRangeArgs 
        { 
            Path = testFile, 
            StartLine = 2, 
            EndLine = 4,
            IncludeLineNumbers = false
        });

        Assert.True(result.Succeeded);
        var lines = result.Content.Split(Environment.NewLine);
        Assert.Equal(["line2", "line3", "line4"], lines);
    }

    [Fact]
    public void FsReadRange_WithLineNumbers_IncludesLineNumbers()
    {
        var testFile = Path.Combine(_testDir, "test.txt");
        File.WriteAllLines(testFile, ["line1", "line2", "line3"]);

        var result = FilesystemHandlers.ReadRange(new FsReadRangeArgs 
        { 
            Path = testFile, 
            StartLine = 1, 
            EndLine = 2,
            IncludeLineNumbers = true
        });

        Assert.True(result.Succeeded);
        Assert.Contains("1: line1", result.Content);
        Assert.Contains("2: line2", result.Content);
    }

    [Fact]
    public void FsWriteRange_ReplacesLines()
    {
        var testFile = Path.Combine(_testDir, "test.txt");
        File.WriteAllLines(testFile, ["line1", "line2", "line3", "line4"]);

        var result = FilesystemHandlers.WriteRange(new FsWriteRangeArgs 
        { 
            Path = testFile, 
            StartLine = 2, 
            EndLine = 3,
            Content = "newline2\nnewline3"
        });

        Assert.True(result.Succeeded);
        var lines = File.ReadAllLines(testFile);
        Assert.Equal(["line1", "newline2", "newline3", "line4"], lines);
    }

    [Fact]
    public void FsWriteRange_InsertsWithoutEndLine()
    {
        var testFile = Path.Combine(_testDir, "test.txt");
        File.WriteAllLines(testFile, ["line1", "line2"]);

        var result = FilesystemHandlers.WriteRange(new FsWriteRangeArgs 
        { 
            Path = testFile, 
            StartLine = 2, 
            EndLine = null,
            Content = "inserted"
        });

        Assert.True(result.Succeeded);
        var lines = File.ReadAllLines(testFile);
        Assert.Equal(["line1", "inserted", "line2"], lines);
    }

    [Fact]
    public void FsDeleteFile_DeletesExistingFile()
    {
        var testFile = Path.Combine(_testDir, "test.txt");
        File.WriteAllText(testFile, "content");

        var result = FilesystemHandlers.DeleteFile(new FsDeleteFileArgs { Path = testFile });

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(testFile));
    }

    [Fact]
    public void FsMoveFile_MovesFile()
    {
        var sourceFile = Path.Combine(_testDir, "source.txt");
        var destFile = Path.Combine(_testDir, "dest.txt");
        File.WriteAllText(sourceFile, "content");

        var result = FilesystemHandlers.MoveFile(new FsMoveFileArgs 
        { 
            SourcePath = sourceFile, 
            DestinationPath = destFile 
        });

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(sourceFile));
        Assert.True(File.Exists(destFile));
        Assert.Equal("content", File.ReadAllText(destFile));
    }

    [Fact]
    public void FsCopyFile_CopiesFile()
    {
        var sourceFile = Path.Combine(_testDir, "source.txt");
        var destFile = Path.Combine(_testDir, "dest.txt");
        File.WriteAllText(sourceFile, "content");

        var result = FilesystemHandlers.CopyFile(new FsCopyFileArgs 
        { 
            SourcePath = sourceFile, 
            DestinationPath = destFile 
        });

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(sourceFile));
        Assert.True(File.Exists(destFile));
        Assert.Equal("content", File.ReadAllText(destFile));
    }

    [Fact]
    public void FsCreateDirectory_CreatesDirectory()
    {
        var newDir = Path.Combine(_testDir, "newdir");

        var result = FilesystemHandlers.CreateDirectory(new FsCreateDirectoryArgs { Path = newDir });

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(newDir));
    }

    [Fact]
    public void FsDeleteDirectory_DeletesRecursively()
    {
        var dirToDelete = Path.Combine(_testDir, "todelete");
        Directory.CreateDirectory(Path.Combine(dirToDelete, "subdir"));
        File.WriteAllText(Path.Combine(dirToDelete, "file.txt"), "content");

        var result = FilesystemHandlers.DeleteDirectory(new FsDeleteDirectoryArgs { Path = dirToDelete });

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(dirToDelete));
    }

    [Fact]
    public void FsLineCount_ReturnsCorrectCount()
    {
        var testFile = Path.Combine(_testDir, "test.txt");
        File.WriteAllLines(testFile, ["line1", "line2", "line3"]);

        var result = FilesystemHandlers.LineCount(new FsLineCountArgs { Path = testFile });

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.LineCount);
    }

    [Fact]
    public void FsListDir_ListsContents()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, "subdir"));
        File.WriteAllText(Path.Combine(_testDir, "file.txt"), "content");

        var result = FilesystemHandlers.ListDir(new FsListDirArgs { Path = _testDir });

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Entries.Count);
        Assert.Contains(result.Entries, e => e.Name == "subdir" && e.IsDirectory);
        Assert.Contains(result.Entries, e => e.Name == "file.txt" && !e.IsDirectory);
    }
}
