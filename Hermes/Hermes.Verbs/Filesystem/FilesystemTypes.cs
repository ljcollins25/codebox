using Hermes.Core;

namespace Hermes.Verbs.Filesystem;

// fs.exists
public sealed class FsExistsArgs
{
    public required string Path { get; init; }
}

public sealed class FsExistsResult : VerbResult
{
    public required bool Exists { get; init; }
}

// fs.readFile
public sealed class FsReadFileArgs
{
    public required string Path { get; init; }
}

public sealed class FsReadFileResult : VerbResult
{
    public required string Content { get; init; }
}

// fs.readRange
public sealed class FsReadRangeArgs
{
    public required string Path { get; init; }
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
    public bool IncludeLineNumbers { get; init; } = true;
}

public sealed class FsReadRangeResult : VerbResult
{
    public required string Content { get; init; }
}

// fs.writeFile
public sealed class FsWriteFileArgs
{
    public required string Path { get; init; }
    public required string Content { get; init; }
}

public sealed class FsWriteFileResult : VerbResult
{
}

// fs.writeRange
public sealed class FsWriteRangeArgs
{
    public required string Path { get; init; }
    public required int StartLine { get; init; }
    public int? EndLine { get; init; }
    public required string Content { get; init; }
}

public sealed class FsWriteRangeResult : VerbResult
{
}

// fs.deleteFile
public sealed class FsDeleteFileArgs
{
    public required string Path { get; init; }
}

public sealed class FsDeleteFileResult : VerbResult
{
}

// fs.moveFile
public sealed class FsMoveFileArgs
{
    public required string SourcePath { get; init; }
    public required string DestinationPath { get; init; }
}

public sealed class FsMoveFileResult : VerbResult
{
}

// fs.copyFile
public sealed class FsCopyFileArgs
{
    public required string SourcePath { get; init; }
    public required string DestinationPath { get; init; }
}

public sealed class FsCopyFileResult : VerbResult
{
}

// fs.createDirectory
public sealed class FsCreateDirectoryArgs
{
    public required string Path { get; init; }
}

public sealed class FsCreateDirectoryResult : VerbResult
{
}

// fs.deleteDirectory
public sealed class FsDeleteDirectoryArgs
{
    public required string Path { get; init; }
}

public sealed class FsDeleteDirectoryResult : VerbResult
{
}

// fs.lineCount
public sealed class FsLineCountArgs
{
    public required string Path { get; init; }
}

public sealed class FsLineCountResult : VerbResult
{
    public required int LineCount { get; init; }
}

// fs.listDir
public sealed class FsListDirArgs
{
    public required string Path { get; init; }
}

public sealed class FsListDirResult : VerbResult
{
    public required IReadOnlyList<DirEntry> Entries { get; init; }
}

public sealed class DirEntry
{
    public required string Name { get; init; }
    public required bool IsDirectory { get; init; }
}
