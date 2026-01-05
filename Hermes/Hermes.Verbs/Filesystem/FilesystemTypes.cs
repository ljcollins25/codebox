using System.ComponentModel;
using Hermes.Core;

namespace Hermes.Verbs.Filesystem;

/// <summary>
/// Arguments for the fs.exists VeRB.
/// </summary>
[Description("Check if a file or directory exists at the specified path.")]
public sealed class FsExistsArgs
{
    /// <summary>
    /// The path to check for existence.
    /// </summary>
    [Description("The absolute or relative path to check for existence.")]
    public required string Path { get; init; }
}

/// <summary>
/// Result of the fs.exists VeRB.
/// </summary>
[Description("Result indicating whether the path exists.")]
public sealed class FsExistsResult : VerbResult
{
    /// <summary>
    /// True if the file or directory exists, false otherwise.
    /// </summary>
    [Description("True if the file or directory exists at the specified path.")]
    public required bool Exists { get; init; }
}

/// <summary>
/// Arguments for the fs.readFile VeRB.
/// </summary>
[Description("Read the entire contents of a file.")]
public sealed class FsReadFileArgs
{
    /// <summary>
    /// The path to the file to read.
    /// </summary>
    [Description("The absolute or relative path to the file to read.")]
    public required string Path { get; init; }
}

/// <summary>
/// Result of the fs.readFile VeRB.
/// </summary>
[Description("The contents of the file.")]
public sealed class FsReadFileResult : VerbResult
{
    /// <summary>
    /// The contents of the file as a string.
    /// </summary>
    [Description("The full text content of the file.")]
    public required string Content { get; init; }
}

/// <summary>
/// Arguments for the fs.readRange VeRB.
/// </summary>
[Description("Read a specific range of lines from a file.")]
public sealed class FsReadRangeArgs
{
    /// <summary>
    /// The path to the file to read.
    /// </summary>
    [Description("The absolute or relative path to the file to read.")]
    public required string Path { get; init; }

    /// <summary>
    /// The 1-based line number to start reading from.
    /// </summary>
    [Description("The 1-based line number to start reading from (inclusive).")]
    public required int StartLine { get; init; }

    /// <summary>
    /// The 1-based line number to stop reading at (inclusive).
    /// </summary>
    [Description("The 1-based line number to stop reading at (inclusive).")]
    public required int EndLine { get; init; }

    /// <summary>
    /// Whether to prefix each line with its line number.
    /// </summary>
    [Description("Whether to prefix each line with its line number. Defaults to true.")]
    public bool IncludeLineNumbers { get; init; } = true;
}

/// <summary>
/// Result of the fs.readRange VeRB.
/// </summary>
[Description("The contents of the specified line range.")]
public sealed class FsReadRangeResult : VerbResult
{
    /// <summary>
    /// The contents of the specified line range.
    /// </summary>
    [Description("The text content of the specified line range.")]
    public required string Content { get; init; }
}

/// <summary>
/// Arguments for the fs.writeFile VeRB.
/// </summary>
[Description("Write content to a file, creating parent directories if needed.")]
public sealed class FsWriteFileArgs
{
    /// <summary>
    /// The path to the file to write.
    /// </summary>
    [Description("The absolute or relative path to the file to write. Parent directories will be created if they don't exist.")]
    public required string Path { get; init; }

    /// <summary>
    /// The content to write to the file.
    /// </summary>
    [Description("The text content to write to the file.")]
    public required string Content { get; init; }
}

/// <summary>
/// Result of the fs.writeFile VeRB.
/// </summary>
[Description("Result of writing content to a file.")]
public sealed class FsWriteFileResult : VerbResult
{
}

/// <summary>
/// Arguments for the fs.writeRange VeRB.
/// </summary>
[Description("Write content to a specific range of lines in a file.")]
public sealed class FsWriteRangeArgs
{
    /// <summary>
    /// The path to the file to modify.
    /// </summary>
    [Description("The absolute or relative path to the file to modify.")]
    public required string Path { get; init; }

    /// <summary>
    /// The 1-based line number to start writing at.
    /// </summary>
    [Description("The 1-based line number to start writing at.")]
    public required int StartLine { get; init; }

    /// <summary>
    /// The 1-based line number to stop replacing at (inclusive). If null, content is inserted without replacing.
    /// </summary>
    [Description("The 1-based line number to stop replacing at (inclusive). If null, content is inserted at StartLine without replacing existing lines.")]
    public int? EndLine { get; init; }

    /// <summary>
    /// The content to write.
    /// </summary>
    [Description("The text content to write. May contain multiple lines separated by newlines.")]
    public required string Content { get; init; }
}

/// <summary>
/// Result of the fs.writeRange VeRB.
/// </summary>
[Description("Result of writing content to a line range.")]
public sealed class FsWriteRangeResult : VerbResult
{
}

/// <summary>
/// Arguments for the fs.deleteFile VeRB.
/// </summary>
[Description("Delete a file.")]
public sealed class FsDeleteFileArgs
{
    /// <summary>
    /// The path to the file to delete.
    /// </summary>
    [Description("The absolute or relative path to the file to delete.")]
    public required string Path { get; init; }
}

/// <summary>
/// Result of the fs.deleteFile VeRB.
/// </summary>
[Description("Result of deleting a file.")]
public sealed class FsDeleteFileResult : VerbResult
{
}

/// <summary>
/// Arguments for the fs.moveFile VeRB.
/// </summary>
[Description("Move or rename a file.")]
public sealed class FsMoveFileArgs
{
    /// <summary>
    /// The path to the file to move.
    /// </summary>
    [Description("The absolute or relative path to the source file.")]
    public required string SourcePath { get; init; }

    /// <summary>
    /// The destination path for the file.
    /// </summary>
    [Description("The absolute or relative path to move the file to. Parent directories will be created if they don't exist.")]
    public required string DestinationPath { get; init; }
}

/// <summary>
/// Result of the fs.moveFile VeRB.
/// </summary>
[Description("Result of moving a file.")]
public sealed class FsMoveFileResult : VerbResult
{
}

/// <summary>
/// Arguments for the fs.copyFile VeRB.
/// </summary>
[Description("Copy a file to a new location.")]
public sealed class FsCopyFileArgs
{
    /// <summary>
    /// The path to the file to copy.
    /// </summary>
    [Description("The absolute or relative path to the source file.")]
    public required string SourcePath { get; init; }

    /// <summary>
    /// The destination path for the copy.
    /// </summary>
    [Description("The absolute or relative path to copy the file to. Parent directories will be created if they don't exist.")]
    public required string DestinationPath { get; init; }
}

/// <summary>
/// Result of the fs.copyFile VeRB.
/// </summary>
[Description("Result of copying a file.")]
public sealed class FsCopyFileResult : VerbResult
{
}

/// <summary>
/// Arguments for the fs.createDirectory VeRB.
/// </summary>
[Description("Create a directory, including any necessary parent directories.")]
public sealed class FsCreateDirectoryArgs
{
    /// <summary>
    /// The path of the directory to create.
    /// </summary>
    [Description("The absolute or relative path of the directory to create. Parent directories will be created if they don't exist.")]
    public required string Path { get; init; }
}

/// <summary>
/// Result of the fs.createDirectory VeRB.
/// </summary>
[Description("Result of creating a directory.")]
public sealed class FsCreateDirectoryResult : VerbResult
{
}

/// <summary>
/// Arguments for the fs.deleteDirectory VeRB.
/// </summary>
[Description("Delete a directory and all its contents recursively.")]
public sealed class FsDeleteDirectoryArgs
{
    /// <summary>
    /// The path of the directory to delete.
    /// </summary>
    [Description("The absolute or relative path of the directory to delete.")]
    public required string Path { get; init; }
}

/// <summary>
/// Result of the fs.deleteDirectory VeRB.
/// </summary>
[Description("Result of deleting a directory.")]
public sealed class FsDeleteDirectoryResult : VerbResult
{
}

/// <summary>
/// Arguments for the fs.lineCount VeRB.
/// </summary>
[Description("Count the number of lines in a file.")]
public sealed class FsLineCountArgs
{
    /// <summary>
    /// The path to the file.
    /// </summary>
    [Description("The absolute or relative path to the file to count lines in.")]
    public required string Path { get; init; }
}

/// <summary>
/// Result of the fs.lineCount VeRB.
/// </summary>
[Description("The number of lines in the file.")]
public sealed class FsLineCountResult : VerbResult
{
    /// <summary>
    /// The number of lines in the file.
    /// </summary>
    [Description("The total number of lines in the file.")]
    public required int LineCount { get; init; }
}

/// <summary>
/// Arguments for the fs.listDir VeRB.
/// </summary>
[Description("List the contents of a directory.")]
public sealed class FsListDirArgs
{
    /// <summary>
    /// The path to the directory to list.
    /// </summary>
    [Description("The absolute or relative path to the directory to list.")]
    public required string Path { get; init; }
}

/// <summary>
/// Result of the fs.listDir VeRB.
/// </summary>
[Description("The contents of the directory.")]
public sealed class FsListDirResult : VerbResult
{
    /// <summary>
    /// The entries in the directory.
    /// </summary>
    [Description("The list of files and subdirectories in the directory.")]
    public required IReadOnlyList<DirEntry> Entries { get; init; }
}

/// <summary>
/// Represents a directory entry (file or subdirectory).
/// </summary>
[Description("A file or subdirectory entry within a directory.")]
public sealed class DirEntry
{
    /// <summary>
    /// The name of the entry (file or directory name only, not the full path).
    /// </summary>
    [Description("The name of the file or directory (not the full path).")]
    public required string Name { get; init; }

    /// <summary>
    /// True if this entry is a directory, false if it's a file.
    /// </summary>
    [Description("True if this entry is a directory, false if it's a file.")]
    public required bool IsDirectory { get; init; }
}
