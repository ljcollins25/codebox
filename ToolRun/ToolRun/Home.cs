using System.Text.Json;
using Tool2App;

namespace ToolRun;

/// <summary>The toolrun cache: tools/&lt;id&gt;/&lt;version&gt;/, dotnet/ (the private dotnet root), apphost/, packages/ (downloads in flight).</summary>
public sealed class ToolRunHome
{
    public string Root { get; }
    public string Tools => Path.Combine(Root, "tools");
    /// <summary>toolrun's private dotnet root: host/fxr/&lt;v&gt;, shared/&lt;framework&gt;/&lt;v&gt;.</summary>
    public string DotnetRoot => Path.Combine(Root, "dotnet");
    public string AppHostTemplate => Path.Combine(Root, "apphost");
    public string Packages => Path.Combine(Root, "packages");
    public string ConfigFile => Path.Combine(Root, "config.json");

    public ToolRunHome(string root) { Root = Path.GetFullPath(root); }

    public static ToolRunHome Default(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath)) return new ToolRunHome(explicitPath);
        var env = Environment.GetEnvironmentVariable("TOOLRUN_HOME");
        if (!string.IsNullOrWhiteSpace(env)) return new ToolRunHome(env);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();
        return new ToolRunHome(Path.Combine(local, "toolrun"));
    }

    public string ToolDir(string id, string version) => Path.Combine(Tools, id.ToLowerInvariant(), version.ToLowerInvariant());

    /// <summary>Removes a downloaded .nupkg and the now empty id/version folders above it.</summary>
    public static void DeleteDownload(string nupkg)
    {
        try
        {
            File.Delete(nupkg);
            var d = Path.GetDirectoryName(nupkg);
            for (int i = 0; i < 2 && d is not null && Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any(); i++)
            {
                Directory.Delete(d);
                d = Path.GetDirectoryName(d);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static long SizeOf(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            try { total += new FileInfo(f).Length; } catch (IOException) { }
        }
        return total;
    }

    public static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" :
        bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.0} MB" :
        bytes >= 1L << 10 ? $"{bytes / (double)(1L << 10):0.0} KB" : $"{bytes} B";

    /// <summary>Deletes a folder that may contain symbolic links without following them.</summary>
    public static void DeleteDir(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var d in Directory.EnumerateDirectories(dir))
        {
            if (new DirectoryInfo(d).LinkTarget is not null) Directory.Delete(d);
            else DeleteDir(d);
        }
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            File.Delete(f);
        }
        Directory.Delete(dir);
    }
}

/// <summary>Optional <home>/config.json (or --config): { "sources": [...], "runtimeSources": [...] }.</summary>
public sealed record ToolRunConfig(IReadOnlyList<string> Sources, IReadOnlyList<string> RuntimeSources)
{
    public static ToolRunConfig Empty { get; } = new(Array.Empty<string>(), Array.Empty<string>());

    public static ToolRunConfig Load(string path)
    {
        if (!File.Exists(path)) return Empty;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            IReadOnlyList<string> Read(string name) =>
                doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Array
                    ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                    : Array.Empty<string>();
            return new ToolRunConfig(Read("sources"), Read("runtimeSources"));
        }
        catch (JsonException ex) { throw new ToolException($"Cannot read {path}: {ex.Message}", ex); }
    }
}
