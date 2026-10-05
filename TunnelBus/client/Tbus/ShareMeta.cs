using System.Text.RegularExpressions;

namespace Tbus;

/// <summary>Optional metadata the bus dashboard shows. Null = not given (the bus keeps what it has).</summary>
internal sealed record ShareMeta(string? Description = null, string? Label = null, string? Kind = null, string? Owner = null)
{
    public const int MaxDescription = 200, MaxLabel = 60, MaxOwner = 100, MaxKind = 24;
    private static readonly Regex KindRx = new("^[a-z0-9][a-z0-9._-]*$", RegexOptions.Compiled);
    public static readonly string[] ValueOptions = ["description", "label", "kind", "owner"];

    public bool IsEmpty => Description == null && Label == null && Kind == null && Owner == null;

    /// <summary>From parsed options (--description, --label, --kind, --owner); owner falls back to TBUS_OWNER.</summary>
    public static ShareMeta FromOptions(IReadOnlyDictionary<string, string?> o, Func<string, string?>? getEnv = null)
    {
        string? Get(string k) => o.TryGetValue(k, out var v) ? v.Trim() : null;
        var owner = Get("owner") ?? (string.IsNullOrWhiteSpace(getEnv?.Invoke("TBUS_OWNER")) ? null : getEnv!("TBUS_OWNER")!.Trim());
        var m = new ShareMeta(Get("description"), Get("label"), Get("kind")?.ToLowerInvariant(), owner);
        m.Validate();
        return m;
    }

    public void Validate()
    {
        if (Description is { } d && d.Length > MaxDescription) throw new UserError($"--description is {d.Length} characters; the bus accepts up to {MaxDescription}.");
        if (Label is { } l && l.Length > MaxLabel) throw new UserError($"--label is {l.Length} characters; the bus accepts up to {MaxLabel}.");
        if (Owner is { } w && w.Length > MaxOwner) throw new UserError($"--owner is {w.Length} characters; the bus accepts up to {MaxOwner}.");
        if (Kind is { Length: > 0 } k && (k.Length > MaxKind || !KindRx.IsMatch(k)))
            throw new UserError($"Bad --kind '{k}': lowercase letters, digits, '.', '_' or '-', up to {MaxKind} characters (e.g. hexad, app, vscode).");
    }

    /// <summary>The JSON fields to send: only what was given.</summary>
    public Dictionary<string, object?> ToFields()
    {
        var f = new Dictionary<string, object?>();
        if (Description != null) f["description"] = Description;
        if (Label != null) f["label"] = Label;
        if (Kind != null) f["kind"] = Kind;
        if (Owner != null) f["owner"] = Owner;
        return f;
    }
}
