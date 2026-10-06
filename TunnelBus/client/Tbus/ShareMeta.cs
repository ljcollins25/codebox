using System.Text.RegularExpressions;

namespace Tbus;

/// <summary>Optional metadata the bus dashboard shows. Null = not given (the bus keeps what it has).</summary>
internal sealed record ShareMeta(string? Description = null, string? Label = null, string? Kind = null, string? Owner = null,
    string? SessionName = null, string? SessionId = null, string? Hexad = null, string? SessionUrl = null)
{
    public const int MaxDescription = 200, MaxLabel = 60, MaxOwner = 100, MaxKind = 24;
    private static readonly Regex KindRx = new("^[a-z0-9][a-z0-9._-]*$", RegexOptions.Compiled);
    public static readonly string[] ValueOptions = ["description", "label", "kind", "owner", "session-name", "session-id", "hexad", "session-url"];

    public bool IsEmpty => ToFields().Count == 0;
    private bool HasSession => SessionName != null || SessionId != null || Hexad != null;

    /// <summary>From parsed options (--description, --label, --kind, --owner); owner falls back to TBUS_OWNER.</summary>
    public static ShareMeta FromOptions(IReadOnlyDictionary<string, string?> o, Func<string, string?>? getEnv = null)
    {
        string? Get(string k) => o.TryGetValue(k, out var v) ? v.Trim() : null;
        var owner = Get("owner") ?? (string.IsNullOrWhiteSpace(getEnv?.Invoke("TBUS_OWNER")) ? null : getEnv!("TBUS_OWNER")!.Trim());
        string? Env(string k) => string.IsNullOrWhiteSpace(getEnv?.Invoke(k)) ? null : getEnv!(k)!.Trim();
        // the session options default to TBUS_SESSION_NAME, TBUS_SESSION_ID, TBUS_HEXAD, TBUS_SESSION_URL (set by hexad in a session)
        var m = new ShareMeta(Get("description"), Get("label"), Get("kind")?.ToLowerInvariant(), owner,
            Get("session-name") ?? Env("TBUS_SESSION_NAME"), Get("session-id") ?? Env("TBUS_SESSION_ID"), Get("hexad") ?? Env("TBUS_HEXAD"), Get("session-url") ?? Env("TBUS_SESSION_URL"));
        m.Validate();
        return m;
    }

    public void Validate()
    {
        if (Description is { } d && d.Length > MaxDescription) throw new UserError($"--description is {d.Length} characters; the bus accepts up to {MaxDescription}.");
        if (Label is { } l && l.Length > MaxLabel) throw new UserError($"--label is {l.Length} characters; the bus accepts up to {MaxLabel}.");
        if (Owner is { } w && w.Length > MaxOwner) throw new UserError($"--owner is {w.Length} characters; the bus accepts up to {MaxOwner}.");
        if (SessionName is { Length: > 60 }) throw new UserError("--session-name is longer than 60 characters.");
        if (SessionId is { Length: > 80 }) throw new UserError("--session-id is longer than 80 characters.");
        if (Hexad is { Length: > 100 }) throw new UserError("--hexad is longer than 100 characters.");
        if (SessionUrl is { Length: > 0 } su && (su.Length > 300 || !Uri.TryCreate(su, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            throw new UserError("--session-url must be an http(s) URL of at most 300 characters.");
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
        if (HasSession)
        {
            var s = new Dictionary<string, string>();
            if (SessionName != null) s["name"] = SessionName;
            if (SessionId != null) s["id"] = SessionId;
            if (Hexad != null) s["hexad"] = Hexad;
            f["session"] = s;
        }
        if (SessionUrl != null) f["sessionUrl"] = SessionUrl;
        return f;
    }
}
