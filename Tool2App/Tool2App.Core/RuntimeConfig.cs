using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tool2App;

public sealed record FrameworkRef(string Name, string Version)
{
    public const string NetCore = "Microsoft.NETCore.App";
    public const string AspNetCore = "Microsoft.AspNetCore.App";
    public const string WindowsDesktop = "Microsoft.WindowsDesktop.App";

    /// <summary>Runtime pack id for a rid: Microsoft.NETCore.App.Runtime.linux-x64 and so on.</summary>
    public string RuntimePackId(Rid rid)
    {
        switch (Name)
        {
            case NetCore: case AspNetCore:
                return Name + ".Runtime." + rid.PackRid;
            case WindowsDesktop:
                if (!rid.IsWindows) throw new ToolException($"The tool needs {WindowsDesktop}, which exists only for Windows rids (not {rid}).");
                return Name + ".Runtime." + rid.PackRid;
            default:
                throw new ToolException($"The tool needs the shared framework '{Name}', which has no runtime pack tool2app knows (supported: {NetCore}, {AspNetCore}, {WindowsDesktop}).");
        }
    }

    /// <summary>Frameworks a framework itself builds on (listed in its own runtimeconfig, which tools do not repeat).</summary>
    public static IReadOnlyList<string> DependenciesOf(string name) => name switch
    {
        AspNetCore or WindowsDesktop => new[] { NetCore },
        _ => Array.Empty<string>(),
    };

    /// <summary>The framework set plus everything they depend on, at the version of the framework that pulled them in.</summary>
    public static IReadOnlyList<FrameworkRef> Expand(IEnumerable<FrameworkRef> frameworks)
    {
        var result = new List<FrameworkRef>();
        foreach (var f in frameworks)
        {
            if (!result.Any(r => r.Name == f.Name)) result.Add(f);
            foreach (var dep in DependenciesOf(f.Name))
                if (!result.Any(r => r.Name == dep) && !frameworks.Any(x => x.Name == dep)) result.Add(new FrameworkRef(dep, f.Version));
        }
        return result;
    }
}

/// <summary>The part of an app's *.runtimeconfig.json that decides which shared frameworks it needs, and its rewriting for self-contained use.</summary>
public sealed class RuntimeConfig
{
    private readonly JsonObject _root;

    public string? Tfm { get; }
    public IReadOnlyList<FrameworkRef> Frameworks { get; }
    public string? RollForward { get; }
    public bool IsSelfContained { get; }

    private RuntimeConfig(JsonObject root, string? tfm, IReadOnlyList<FrameworkRef> frameworks, string? rollForward, bool selfContained)
    {
        _root = root; Tfm = tfm; Frameworks = frameworks; RollForward = rollForward; IsSelfContained = selfContained;
    }

    public static RuntimeConfig Parse(string json)
    {
        JsonObject root;
        try { root = JsonNode.Parse(json, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?.AsObject() ?? new JsonObject(); }
        catch (JsonException ex) { throw new ToolException("runtimeconfig.json is not valid JSON: " + ex.Message, ex); }
        var opts = root["runtimeOptions"] as JsonObject ?? new JsonObject();
        var frameworks = new List<FrameworkRef>();
        string? rollForward = (string?)opts["rollForward"];
        void AddFramework(JsonNode? n)
        {
            if (n is not JsonObject o) return;
            var name = (string?)o["name"]; var version = (string?)o["version"];
            if (name is null || version is null) return;
            frameworks.Add(new FrameworkRef(name, version));
            rollForward ??= (string?)o["rollForward"];
        }
        AddFramework(opts["framework"]);
        if (opts["frameworks"] is JsonArray arr) foreach (var n in arr) AddFramework(n);
        return new RuntimeConfig(root, (string?)opts["tfm"], frameworks, rollForward, opts["includedFrameworks"] is JsonArray);
    }

    /// <summary>For a tool without a runtimeconfig: assume the plain runtime of its TFM.</summary>
    public static RuntimeConfig FromTfm(Tfm tfm) =>
        new(new JsonObject { ["runtimeOptions"] = new JsonObject { ["tfm"] = tfm.Text.Split('-')[0] } }, tfm.Text.Split('-')[0],
            new[] { new FrameworkRef(FrameworkRef.NetCore, $"{tfm.Version.Major}.{tfm.Version.Minor}.0") }, null, false);

    /// <summary>
    /// Self-contained form: "framework"/"frameworks" and the roll-forward settings are replaced by "includedFrameworks"
    /// (what the SDK writes for SelfContained); tfm, configProperties and the rest are kept.
    /// </summary>
    public string RewriteSelfContained(IEnumerable<FrameworkRef> included)
    {
        var copy = JsonNode.Parse(_root.ToJsonString())!.AsObject();
        var opts = copy["runtimeOptions"] as JsonObject ?? new JsonObject();
        copy["runtimeOptions"] = opts;
        foreach (var key in new[] { "framework", "frameworks", "rollForward", "rollForwardOnNoCandidateFx", "applyPatches" }) opts.Remove(key);
        var arr = new JsonArray();
        foreach (var f in included) arr.Add(new JsonObject { ["name"] = f.Name, ["version"] = f.Version });
        opts["includedFrameworks"] = arr;
        return copy.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}

/// <summary>Chooses the runtime version to ship.</summary>
public static class RuntimeVersions
{
    /// <summary>
    /// The latest patch of the requested major.minor. If that line does not exist on the feed, the tool's rollForward
    /// setting decides (Minor/LatestMinor: a later minor of the same major; Major/LatestMajor: any later line);
    /// otherwise this throws. Prereleases are used only when asked for or when the requested version is one.
    /// </summary>
    public static string Select(string requested, IEnumerable<string> available, string? rollForward = null, bool allowPrerelease = false)
    {
        var req = SemVer.Parse(requested);
        bool pre = allowPrerelease || req.IsPrerelease;
        var versions = available.Select(a => SemVer.TryParse(a, out var v) ? v : null).Where(v => v is not null && (pre || !v.IsPrerelease)).Select(v => v!).ToList();

        var sameLine = versions.Where(v => v.Major == req.Major && v.Minor == req.Minor).OrderBy(v => v).ToList();
        if (sameLine.Count > 0) return sameLine[^1].Text;

        var policy = (rollForward ?? "Minor").ToLowerInvariant();
        IEnumerable<SemVer> later = Enumerable.Empty<SemVer>();
        if (policy is "minor" or "latestminor" or "major" or "latestmajor")
            later = versions.Where(v => v.Major == req.Major && v.Minor > req.Minor);
        if (!later.Any() && policy is "major" or "latestmajor")
            later = versions.Where(v => v.Major > req.Major);
        var pool = later.ToList();
        if (pool.Count > 0)
        {
            bool latest = policy.StartsWith("latest");
            var line = (latest ? pool.OrderByDescending(v => v.Major).ThenByDescending(v => v.Minor) : pool.OrderBy(v => v.Major).ThenBy(v => v.Minor)).First();
            return pool.Where(v => v.Major == line.Major && v.Minor == line.Minor).Max()!.Text;
        }
        throw new ToolException($"No runtime {req.Major}.{req.Minor}.x is available (rollForward={rollForward ?? "default"}); newest available: "
            + (versions.Count == 0 ? "none" : versions.Max()!.Text) + ". Use --runtime-version to choose one.");
    }

    /// <summary>An explicit --runtime-version: must exist on the feed.</summary>
    public static string Pin(string version, IEnumerable<string> available)
    {
        var want = SemVer.Parse(version);
        var match = available.Select(a => SemVer.TryParse(a, out var v) ? v : null).FirstOrDefault(v => v is not null && v.CompareTo(want) == 0);
        if (match is null) throw new ToolException($"Runtime version {version} is not available on the feed.");
        return match.Text;
    }
}
