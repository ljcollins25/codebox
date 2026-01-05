namespace Hermes.Verbs.Filesystem;

/// <summary>
/// Unified filesystem VeRB handlers. All fs.* operations are implemented here.
/// </summary>
public static class FilesystemHandlers
{
    /// <summary>
    /// fs.exists - Check if a file or directory exists.
    /// </summary>
    public static FsExistsResult Exists(FsExistsArgs args)
    {
        var path = PathHelper.NormalizePath(args.Path);
        var exists = File.Exists(path) || Directory.Exists(path);
        return new FsExistsResult { Exists = exists };
    }

    /// <summary>
    /// fs.readFile - Read the entire contents of a file.
    /// </summary>
    public static FsReadFileResult ReadFile(FsReadFileArgs args)
    {
        var path = PathHelper.NormalizePath(args.Path);

        if (!File.Exists(path))
        {
            return new FsReadFileResult
            {
                Content = string.Empty,
                Succeeded = false,
                ErrorMessage = $"File not found: {path}"
            };
        }

        var content = File.ReadAllText(path);
        return new FsReadFileResult { Content = content };
    }

    /// <summary>
    /// fs.readRange - Read a range of lines from a file.
    /// </summary>
    public static FsReadRangeResult ReadRange(FsReadRangeArgs args)
    {
        var path = PathHelper.NormalizePath(args.Path);

        if (!File.Exists(path))
        {
            return new FsReadRangeResult
            {
                Content = string.Empty,
                Succeeded = false,
                ErrorMessage = $"File not found: {path}"
            };
        }

        var lines = File.ReadAllLines(path);
        var startIndex = args.StartLine - 1; // Convert to 0-based
        var endIndex = args.EndLine - 1;

        if (startIndex < 0 || endIndex >= lines.Length || startIndex > endIndex)
        {
            return new FsReadRangeResult
            {
                Content = string.Empty,
                Succeeded = false,
                ErrorMessage = $"Invalid line range: {args.StartLine}-{args.EndLine} (file has {lines.Length} lines)"
            };
        }

        var selectedLines = new List<string>();
        for (int i = startIndex; i <= endIndex; i++)
        {
            if (args.IncludeLineNumbers)
            {
                selectedLines.Add($"{i + 1}: {lines[i]}");
            }
            else
            {
                selectedLines.Add(lines[i]);
            }
        }

        return new FsReadRangeResult { Content = string.Join(Environment.NewLine, selectedLines) };
    }

    /// <summary>
    /// fs.writeFile - Write content to a file, creating parent directories if needed.
    /// </summary>
    public static FsWriteFileResult WriteFile(FsWriteFileArgs args)
    {
        var path = PathHelper.NormalizePath(args.Path);

        // Create parent directories if they don't exist
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, args.Content);
        return new FsWriteFileResult();
    }

    /// <summary>
    /// fs.writeRange - Write content to a range of lines in a file.
    /// </summary>
    public static FsWriteRangeResult WriteRange(FsWriteRangeArgs args)
    {
        var path = PathHelper.NormalizePath(args.Path);

        if (!File.Exists(path))
        {
            return new FsWriteRangeResult
            {
                Succeeded = false,
                ErrorMessage = $"File not found: {path}"
            };
        }

        var lines = File.ReadAllLines(path).ToList();
        var startIndex = args.StartLine - 1; // Convert to 0-based
        var newLines = args.Content.Split(["\r\n", "\n"], StringSplitOptions.None);

        if (args.EndLine.HasValue)
        {
            // Replace range
            var endIndex = args.EndLine.Value - 1;

            if (startIndex < 0 || endIndex >= lines.Count || startIndex > endIndex)
            {
                return new FsWriteRangeResult
                {
                    Succeeded = false,
                    ErrorMessage = $"Invalid line range: {args.StartLine}-{args.EndLine} (file has {lines.Count} lines)"
                };
            }

            // Remove old lines and insert new ones
            lines.RemoveRange(startIndex, endIndex - startIndex + 1);
            lines.InsertRange(startIndex, newLines);
        }
        else
        {
            // Insert without replacing
            if (startIndex < 0 || startIndex > lines.Count)
            {
                return new FsWriteRangeResult
                {
                    Succeeded = false,
                    ErrorMessage = $"Invalid start line: {args.StartLine} (file has {lines.Count} lines)"
                };
            }

            lines.InsertRange(startIndex, newLines);
        }

        File.WriteAllLines(path, lines);
        return new FsWriteRangeResult();
    }

    /// <summary>
    /// fs.deleteFile - Delete a file.
    /// </summary>
    public static FsDeleteFileResult DeleteFile(FsDeleteFileArgs args)
    {
        var path = PathHelper.NormalizePath(args.Path);

        if (!File.Exists(path))
        {
            return new FsDeleteFileResult
            {
                Succeeded = false,
                ErrorMessage = $"File not found: {path}"
            };
        }

        File.Delete(path);
        return new FsDeleteFileResult();
    }

    /// <summary>
    /// fs.moveFile - Move or rename a file.
    /// </summary>
    public static FsMoveFileResult MoveFile(FsMoveFileArgs args)
    {
        var sourcePath = PathHelper.NormalizePath(args.SourcePath);
        var destPath = PathHelper.NormalizePath(args.DestinationPath);

        if (!File.Exists(sourcePath))
        {
            return new FsMoveFileResult
            {
                Succeeded = false,
                ErrorMessage = $"Source file not found: {sourcePath}"
            };
        }

        // Create destination parent directories if they don't exist
        var destDirectory = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDirectory) && !Directory.Exists(destDirectory))
        {
            Directory.CreateDirectory(destDirectory);
        }

        File.Move(sourcePath, destPath, overwrite: true);
        return new FsMoveFileResult();
    }

    /// <summary>
    /// fs.copyFile - Copy a file.
    /// </summary>
    public static FsCopyFileResult CopyFile(FsCopyFileArgs args)
    {
        var sourcePath = PathHelper.NormalizePath(args.SourcePath);
        var destPath = PathHelper.NormalizePath(args.DestinationPath);

        if (!File.Exists(sourcePath))
        {
            return new FsCopyFileResult
            {
                Succeeded = false,
                ErrorMessage = $"Source file not found: {sourcePath}"
            };
        }

        // Create destination parent directories if they don't exist
        var destDirectory = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDirectory) && !Directory.Exists(destDirectory))
        {
            Directory.CreateDirectory(destDirectory);
        }

        File.Copy(sourcePath, destPath, overwrite: true);
        return new FsCopyFileResult();
    }

    /// <summary>
    /// fs.createDirectory - Create a directory.
    /// </summary>
    public static FsCreateDirectoryResult CreateDirectory(FsCreateDirectoryArgs args)
    {
        var path = PathHelper.NormalizePath(args.Path);
        Directory.CreateDirectory(path);
        return new FsCreateDirectoryResult();
    }

    /// <summary>
    /// fs.deleteDirectory - Delete a directory recursively.
    /// </summary>
    public static FsDeleteDirectoryResult DeleteDirectory(FsDeleteDirectoryArgs args)
    {
        var path = PathHelper.NormalizePath(args.Path);

        if (!Directory.Exists(path))
        {
            return new FsDeleteDirectoryResult
            {
                Succeeded = false,
                ErrorMessage = $"Directory not found: {path}"
            };
        }

        Directory.Delete(path, recursive: true);
        return new FsDeleteDirectoryResult();
    }

    /// <summary>
    /// fs.lineCount - Get the number of lines in a file.
    /// </summary>
    public static FsLineCountResult LineCount(FsLineCountArgs args)
    {
        var path = PathHelper.NormalizePath(args.Path);

        if (!File.Exists(path))
        {
            return new FsLineCountResult
            {
                LineCount = 0,
                Succeeded = false,
                ErrorMessage = $"File not found: {path}"
            };
        }

        var lineCount = File.ReadLines(path).Count();
        return new FsLineCountResult { LineCount = lineCount };
    }

    /// <summary>
    /// fs.listDir - List directory contents.
    /// </summary>
    public static FsListDirResult ListDir(FsListDirArgs args)
    {
        var path = PathHelper.NormalizePath(args.Path);

        if (!Directory.Exists(path))
        {
            return new FsListDirResult
            {
                Entries = [],
                Succeeded = false,
                ErrorMessage = $"Directory not found: {path}"
            };
        }

        var entries = new List<DirEntry>();

        foreach (var dir in Directory.GetDirectories(path))
        {
            entries.Add(new DirEntry
            {
                Name = Path.GetFileName(dir),
                IsDirectory = true
            });
        }

        foreach (var file in Directory.GetFiles(path))
        {
            entries.Add(new DirEntry
            {
                Name = Path.GetFileName(file),
                IsDirectory = false
            });
        }

        return new FsListDirResult { Entries = entries };
    }
}
