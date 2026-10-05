using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Tool2App;

public sealed record ToolCommand(string Name, string EntryPoint, string Runner);
public sealed record RidPackageRef(string Rid, string Id);

/// <summary>Contents of tools/&lt;tfm&gt;/&lt;any|rid&gt;/DotnetToolSettings.xml.</summary>
public sealed record ToolSettings(IReadOnlyList<ToolCommand> Commands, IReadOnlyList<RidPackageRef> RidPackages)
{
    public static ToolSettings Parse(string xml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException ex) { throw new ToolException("DotnetToolSettings.xml is not valid XML: " + ex.Message, ex); }
        var commands = doc.Descendants().Where(e => e.Name.LocalName == "Command")
            .Select(e => new ToolCommand((string?)e.Attribute("Name") ?? "", (string?)e.Attribute("EntryPoint") ?? "", (string?)e.Attribute("Runner") ?? "dotnet"))
            .Where(c => c.Name.Length > 0 && c.EntryPoint.Length > 0).ToList();
        var rids = doc.Descendants().Where(e => e.Name.LocalName == "RuntimeIdentifierPackage")
            .Select(e => new RidPackageRef((string?)e.Attribute("RuntimeIdentifier") ?? "", (string?)e.Attribute("Id") ?? ""))
            .Where(r => r.Rid.Length > 0 && r.Id.Length > 0).ToList();
        if (commands.Count == 0 && rids.Count == 0) throw new ToolException("DotnetToolSettings.xml lists no commands.");
        return new ToolSettings(commands, rids);
    }
}

/// <summary>A target framework moniker of a tool: net8.0, net10.0-windows7.0, netcoreapp3.1.</summary>
public sealed record Tfm(string Text, SemVer Version, string? Platform)
{
    private static readonly Regex Pattern = new(@"^(?:net(?<v>\d+\.\d+)|netcoreapp(?<v>\d+\.\d+))(?:-(?<p>.+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool TryParse(string text, out Tfm tfm)
    {
        tfm = null!;
        var m = Pattern.Match(text);
        if (!m.Success) return false;
        // net1.0..net4.x without a dot are .NET Framework, not matched; "net5.0" and up, or netcoreapp, are modern .NET
        var v = SemVer.Parse(m.Groups["v"].Value + ".0");
        if (text.StartsWith("net", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase) && v.Major < 5) return false;
        tfm = new Tfm(text, v, m.Groups["p"].Success ? m.Groups["p"].Value : null);
        return true;
    }
}

/// <summary>One tools/&lt;tfm&gt;/&lt;rid&gt;/ folder of a package that holds a DotnetToolSettings.xml.</summary>
public sealed record ToolAsset(Tfm Tfm, string RidDir, string Prefix, ToolSettings Settings)
{
    public static IReadOnlyList<ToolAsset> Discover(PackageContent pkg)
    {
        var list = new List<ToolAsset>();
        foreach (var f in pkg.Files)
        {
            var parts = f.Split('/');
            if (parts.Length != 4 || parts[0] != "tools" || !parts[3].Equals("DotnetToolSettings.xml", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Tfm.TryParse(parts[1], out var tfm)) continue;
            list.Add(new ToolAsset(tfm, parts[2], string.Join('/', parts[..3]) + "/", ToolSettings.Parse(pkg.ReadText(f))));
        }
        return list;
    }

    /// <summary>
    /// Best asset for <paramref name="rid"/>: only folders "any" or a rid in the target's fallback chain qualify;
    /// highest TFM wins (or exactly <paramref name="tfm"/> if given), then the most specific rid.
    /// </summary>
    public static ToolAsset Select(IReadOnlyList<ToolAsset> all, Rid rid, string? tfm = null)
    {
        if (all.Count == 0) throw new ToolException("The package has no tools/<tfm>/<rid>/DotnetToolSettings.xml; it is not a .NET tool package.");
        var candidates = all.Where(a => rid.Rank(a.RidDir) >= 0).ToList();
        if (tfm is not null)
            candidates = candidates.Where(a => a.Tfm.Text.Equals(tfm, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0)
            throw new ToolException($"No tool asset for {rid}{(tfm is null ? "" : " / " + tfm)}. The package offers: "
                + string.Join(", ", all.Select(a => $"{a.Tfm.Text}/{a.RidDir}")));
        return candidates
            .OrderByDescending(a => a.Tfm.Version)
            .ThenBy(a => rid.Rank(a.RidDir))
            .ThenBy(a => (a.Tfm.Platform is not null) == rid.IsWindows ? 0 : 1)
            .First();
    }

    public ToolCommand PickCommand(string? name)
    {
        if (Settings.Commands.Count == 0) throw new ToolException("This asset lists no commands.");
        if (name is null) return Settings.Commands[0];
        return Settings.Commands.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ToolException($"No command '{name}'; the tool has: {string.Join(", ", Settings.Commands.Select(c => c.Name))}.");
    }
}
