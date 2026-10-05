using System.Globalization;

namespace Tool2App;

/// <summary>NuGet-style version: 1 to 4 numeric parts, optional -prerelease labels, +metadata ignored.</summary>
public sealed class SemVer : IComparable<SemVer>
{
    public int[] Parts { get; }
    public string[] Pre { get; }
    public string Text { get; }
    public int Major => Parts[0];
    public int Minor => Parts[1];
    public int Patch => Parts[2];
    public bool IsPrerelease => Pre.Length > 0;

    private SemVer(int[] parts, string[] pre, string text) { Parts = parts; Pre = pre; Text = text; }

    public static bool TryParse(string? text, out SemVer version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        var plus = s.IndexOf('+'); if (plus >= 0) s = s[..plus];
        string[] pre = Array.Empty<string>();
        var dash = s.IndexOf('-');
        if (dash >= 0) { pre = s[(dash + 1)..].Split('.'); s = s[..dash]; }
        var nums = s.Split('.');
        if (nums.Length is < 1 or > 4) return false;
        var parts = new int[4];
        for (int i = 0; i < nums.Length; i++)
            if (!int.TryParse(nums[i], NumberStyles.None, CultureInfo.InvariantCulture, out parts[i])) return false;
        version = new SemVer(parts, pre, text.Trim());
        return true;
    }

    public static SemVer Parse(string text) =>
        TryParse(text, out var v) ? v : throw new ToolException($"'{text}' is not a valid version.");

    public int CompareTo(SemVer? other)
    {
        if (other is null) return 1;
        for (int i = 0; i < 4; i++)
        {
            var c = Parts[i].CompareTo(other.Parts[i]);
            if (c != 0) return c;
        }
        if (Pre.Length == 0 && other.Pre.Length == 0) return 0;
        if (Pre.Length == 0) return 1;
        if (other.Pre.Length == 0) return -1;
        for (int i = 0; i < Math.Min(Pre.Length, other.Pre.Length); i++)
        {
            bool an = int.TryParse(Pre[i], out var a), bn = int.TryParse(other.Pre[i], out var b);
            int c = an && bn ? a.CompareTo(b) : an ? -1 : bn ? 1 : string.Compare(Pre[i], other.Pre[i], StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
        }
        return Pre.Length.CompareTo(other.Pre.Length);
    }

    public static SemVer? Max(IEnumerable<string> versions, bool includePrerelease)
    {
        SemVer? best = null;
        foreach (var t in versions)
        {
            if (!TryParse(t, out var v)) continue;
            if (v.IsPrerelease && !includePrerelease) continue;
            if (best is null || v.CompareTo(best) > 0) best = v;
        }
        return best;
    }

    public override string ToString() => Text;
}
