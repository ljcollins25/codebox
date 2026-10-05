using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tool2App;

/// <summary>Merging a tool's *.deps.json with the shared frameworks' own deps.json files for a self-contained layout.</summary>
public static class DepsJson
{
    public static string Serialize(JsonObject deps) => deps.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    public static JsonObject Parse(string json) =>
        JsonNode.Parse(json, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?.AsObject()
        ?? throw new ToolException("deps.json is empty.");

    public static bool IsManaged(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var pe = new PEReader(fs);
            return pe.HasMetadata;
        }
        catch (BadImageFormatException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>
    /// Builds the deps.json of a self-contained app.
    /// The tool's target (".NETCoreApp,Version=v8.0") becomes ".NETCoreApp,Version=v8.0/&lt;rid&gt;" and is made runtimeTarget;
    /// each framework's runtime-pack entries and libraries are added; RID-specific assets of the tool that apply to
    /// <paramref name="rid"/> (runtimeTargets) are moved into plain runtime/native lists and their files copied to the app root,
    /// which is how the SDK lays out a RID-specific publish, so the host needs no RID graph to find them.
    /// With <paramref name="tool"/> null a minimal app entry listing <paramref name="appManaged"/> is generated.
    /// </summary>
    public static JsonObject MergeSelfContained(JsonObject? tool, IEnumerable<JsonObject> frameworks, Rid rid, Tfm tfm, string appDir,
        bool includeRuntimeNative, IEnumerable<string>? appManaged = null, string appName = "app")
    {
        var tfmKey = $".NETCoreApp,Version=v{tfm.Version.Major}.{tfm.Version.Minor}";
        string? toolKey = null;
        JsonObject? toolTarget = null;
        if (tool is not null && tool["targets"] is JsonObject targets)
        {
            var name = (string?)tool["runtimeTarget"]?["name"];
            toolKey = name is not null && targets.ContainsKey(name) ? name : targets.Select(t => t.Key).FirstOrDefault(k => !k.Contains('/')) ?? targets.Select(t => t.Key).FirstOrDefault();
            toolTarget = toolKey is null ? null : targets[toolKey] as JsonObject;
            if (toolKey is not null && !toolKey.Contains('/')) tfmKey = toolKey;
            else if (toolKey is not null) tfmKey = toolKey[..toolKey.IndexOf('/')];
        }
        var newKey = tfmKey + "/" + rid.PackRid;

        var result = new JsonObject();
        if (tool is not null) foreach (var kv in tool) if (kv.Key is not ("runtimeTarget" or "targets" or "libraries")) result[kv.Key] = kv.Value?.DeepClone();
        result["runtimeTarget"] = new JsonObject { ["name"] = newKey, ["signature"] = "" };
        result["compilationOptions"] ??= new JsonObject();

        var newTarget = new JsonObject();
        var libraries = new JsonObject();

        if (toolTarget is not null)
        {
            foreach (var (key, node) in toolTarget.ToList())
            {
                if (node is not JsonObject entry) continue;
                var copy = entry.DeepClone().AsObject();
                FlattenRidAssets(copy, rid, appDir);
                newTarget[key] = copy;
            }
            if (tool!["libraries"] is JsonObject libs) foreach (var kv in libs) libraries[kv.Key] = kv.Value?.DeepClone();
        }
        else
        {
            var runtime = new JsonObject();
            foreach (var m in appManaged ?? Array.Empty<string>()) runtime[m] = new JsonObject();
            newTarget[appName + "/1.0.0"] = new JsonObject { ["runtime"] = runtime };
            libraries[appName + "/1.0.0"] = new JsonObject { ["type"] = "project", ["serviceable"] = false, ["sha512"] = "" };
        }

        foreach (var fw in frameworks)
        {
            var fwName = (string?)fw["runtimeTarget"]?["name"];
            var fwTargets = fw["targets"] as JsonObject;
            if (fwTargets is null) continue;
            var fwKey = fwName is not null && fwTargets.ContainsKey(fwName) ? fwName : fwTargets.Select(t => t.Key).FirstOrDefault(k => k.EndsWith("/" + rid.PackRid, StringComparison.Ordinal));
            if (fwKey is null || fwTargets[fwKey] is not JsonObject fwEntries) continue;
            foreach (var (key, node) in fwEntries)
            {
                if (node is not JsonObject entry || newTarget.ContainsKey(key)) continue;
                var copy = entry.DeepClone().AsObject();
                if (!includeRuntimeNative) copy.Remove("native");
                newTarget[key] = copy;
            }
            if (fw["libraries"] is JsonObject fwLibs)
                foreach (var kv in fwLibs) if (!libraries.ContainsKey(kv.Key)) libraries[kv.Key] = kv.Value?.DeepClone();
        }

        result["targets"] = new JsonObject { [newKey] = newTarget };
        result["libraries"] = libraries;
        return result;
    }

    private static void FlattenRidAssets(JsonObject entry, Rid rid, string appDir)
    {
        if (entry["runtimeTargets"] is not JsonObject rts) return;
        // per (asset type, file name) keep the asset from the most specific applicable rid
        var best = new Dictionary<(string type, string file), (int rank, string path)>();
        foreach (var (path, node) in rts)
        {
            var itemRid = (string?)node?["rid"]; var type = (string?)node?["assetType"];
            if (itemRid is null || type is null) continue;
            int rank = rid.Rank(itemRid);
            if (rank < 0) continue;
            var k = (type, path[(path.LastIndexOf('/') + 1)..]);
            if (!best.TryGetValue(k, out var cur) || rank < cur.rank) best[k] = (rank, path);
        }
        foreach (var ((type, file), (_, path)) in best)
        {
            var section = type == "native" ? "native" : type == "runtime" ? "runtime" : null;
            if (section is null) continue;
            var src = Path.Combine(appDir, path.Replace('/', Path.DirectorySeparatorChar));
            var dst = Path.Combine(appDir, file);
            if (File.Exists(src) && !string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.Ordinal))
                File.Move(src, dst, true);   // moved, not copied: the deps entry no longer points at the runtimes/ path
            var asset = rts[path]!.DeepClone().AsObject();
            asset.Remove("rid"); asset.Remove("assetType");
            var target = entry[section] as JsonObject ?? new JsonObject();
            entry[section] = target;
            // the flattened name replaces a rid-neutral file of the same name
            foreach (var existing in target.Select(t => t.Key).Where(k => k[(k.LastIndexOf('/') + 1)..] == file).ToList()) target.Remove(existing);
            target[file] = asset;
        }
        entry.Remove("runtimeTargets");
        PruneEmptyDirectories(Path.Combine(appDir, "runtimes"));
    }

    private static void PruneEmptyDirectories(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var sub in Directory.GetDirectories(dir)) PruneEmptyDirectories(sub);
        if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
    }

    /// <summary>A stand-in framework deps.json for runtime packs that ship none (old netcoreapp packs).</summary>
    public static JsonObject SynthesizeFramework(string packId, string version, Rid rid, Tfm tfm, IEnumerable<string> managed, IEnumerable<string> native)
    {
        var key = $".NETCoreApp,Version=v{tfm.Version.Major}.{tfm.Version.Minor}/{rid.PackRid}";
        var runtime = new JsonObject(); foreach (var m in managed) runtime[m] = new JsonObject();
        var nat = new JsonObject(); foreach (var n in native) nat[n] = new JsonObject();
        var entry = new JsonObject { ["runtime"] = runtime };
        if (nat.Count > 0) entry["native"] = nat;
        return new JsonObject
        {
            ["runtimeTarget"] = new JsonObject { ["name"] = key, ["signature"] = "" },
            ["targets"] = new JsonObject { [key] = new JsonObject { [$"{packId}/{version}"] = entry } },
            ["libraries"] = new JsonObject { [$"{packId}/{version}"] = new JsonObject { ["type"] = "package", ["serviceable"] = true, ["sha512"] = "", ["path"] = $"{packId.ToLowerInvariant()}/{version}" } },
        };
    }
}

/// <summary>Unpacks a runtime pack (Microsoft.NETCore.App.Runtime.&lt;rid&gt; and friends) next to the app.</summary>
public static class RuntimePack
{
    /// <summary>Copies runtimes/&lt;rid&gt;/lib/&lt;tfm&gt;/** and (optionally) runtimes/&lt;rid&gt;/native/** into the app dir; returns the framework's deps.json.</summary>
    public static JsonObject InstallInto(PackageContent pack, string packId, string version, Rid rid, Tfm tfm, string appDir, bool includeNative)
    {
        var libPrefix = $"runtimes/{rid.PackRid}/lib/";
        var nativePrefix = $"runtimes/{rid.PackRid}/native/";
        JsonObject? deps = null;
        var managed = new List<string>(); var native = new List<string>();
        foreach (var f in pack.Files)
        {
            if (f.StartsWith(libPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = f[libPrefix.Length..];
                var slash = rest.IndexOf('/'); if (slash < 0) continue;
                var rel = rest[(slash + 1)..];            // drop the <tfm>/ folder
                if (rel.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) && !rel.Contains('/')) { deps = DepsJson.Parse(pack.ReadText(f)); continue; }
                if (rel.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)) continue;
                pack.CopyTo(f, Path.Combine(appDir, rel.Replace('/', Path.DirectorySeparatorChar)));
                if (!rel.Contains('/')) managed.Add(rel);
            }
            else if (f.StartsWith(nativePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var rel = f[nativePrefix.Length..];
                native.Add(rel);
                if (includeNative) pack.CopyTo(f, Path.Combine(appDir, rel.Replace('/', Path.DirectorySeparatorChar)));
            }
        }
        if (managed.Count == 0 && native.Count == 0) throw new ToolException($"{packId} {version} has nothing for {rid.PackRid} (runtimes/{rid.PackRid}/...).");
        return deps ?? DepsJson.SynthesizeFramework(packId, version, rid, tfm, managed, native);
    }
}
