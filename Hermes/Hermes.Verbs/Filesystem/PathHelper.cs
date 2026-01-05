namespace Hermes.Verbs.Filesystem;

/// <summary>
/// Helper for path normalization and safety checks.
/// </summary>
internal static class PathHelper
{
    /// <summary>
    /// Normalizes a path for the current platform.
    /// </summary>
    public static string NormalizePath(string path)
    {
        // Expand environment variables
        path = Environment.ExpandEnvironmentVariables(path);
        
        // Get full path (handles relative paths, .., etc.)
        path = Path.GetFullPath(path);
        
        return path;
    }
}
