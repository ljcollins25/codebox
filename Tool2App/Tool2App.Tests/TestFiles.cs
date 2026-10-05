using System.IO.Compression;
using System.Net;
using System.Text;

namespace Tool2App.Tests;

internal static class TestFiles
{
    public static string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "t2a-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    public static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>Zip with '/' separated entries (a .nupkg).</summary>
    public static byte[] Zip(IDictionary<string, byte[]> entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, bytes) in entries)
            {
                using var s = z.CreateEntry(name).Open();
                s.Write(bytes);
            }
        return ms.ToArray();
    }

    public static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
}

/// <summary>A tiny real HTTP server (HttpListener on loopback) that speaks the NuGet v2 and v3 protocols for a set of packages.</summary>
internal sealed class FakeNuGetServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _loop;
    private readonly Dictionary<string, SortedDictionary<string, byte[]>> _packages = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    public List<string> Requests { get; } = new();
    public string BaseUrl { get; }
    public int V2PageSize { get; set; } = 2;
    public bool BlockV3 { get; set; }

    public string V2Url => BaseUrl + "v2";
    public string V3Url => BaseUrl + "v3/index.json";

    public FakeNuGetServer()
    {
        for (int attempt = 0; ; attempt++)
        {
            var port = Random.Shared.Next(20000, 60000);
            try
            {
                var l = new HttpListener();
                l.Prefixes.Add($"http://127.0.0.1:{port}/");
                l.Start();
                _listener = l;
                BaseUrl = $"http://127.0.0.1:{port}/";
                break;
            }
            catch (HttpListenerException) when (attempt < 20) { }
        }
        _loop = Task.Run(Loop);
    }

    public void Add(string id, string version, byte[] nupkg)
    {
        lock (_lock)
        {
            if (!_packages.TryGetValue(id, out var d)) _packages[id] = d = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            d[version] = nupkg;
        }
    }

    public int Count(string fragment) { lock (_lock) return Requests.Count(r => r.Contains(fragment, StringComparison.OrdinalIgnoreCase)); }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            try { Handle(ctx); }
            catch (Exception) { try { ctx.Response.StatusCode = 500; } catch { } }
            finally { try { ctx.Response.Close(); } catch { } }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var url = ctx.Request.Url!;
        var path = Uri.UnescapeDataString(url.AbsolutePath);
        lock (_lock) Requests.Add(path + url.Query);
        void Send(string contentType, byte[] body) { ctx.Response.ContentType = contentType; ctx.Response.OutputStream.Write(body); }
        void NotFound() => ctx.Response.StatusCode = 404;

        if (path == "/v3/index.json")
        {
            if (BlockV3) { ctx.Response.StatusCode = 503; return; }
            Send("application/json", TestFiles.Bytes($$"""{"version":"3.0.0","resources":[{"@id":"{{BaseUrl}}v3/search","@type":"SearchQueryService"},{"@id":"{{BaseUrl}}v3/flat/","@type":"PackageBaseAddress/3.0.0"}]}"""));
        }
        else if (path.StartsWith("/v3/flat/"))
        {
            if (BlockV3) { ctx.Response.StatusCode = 503; return; }
            var parts = path["/v3/flat/".Length..].Split('/');
            if (!Find(parts[0], out var versions)) { NotFound(); return; }
            if (parts.Length == 2 && parts[1] == "index.json")
                Send("application/json", TestFiles.Bytes("{\"versions\":[" + string.Join(",", versions.Keys.Select(v => "\"" + v.ToLowerInvariant() + "\"")) + "]}"));
            else if (parts.Length == 3 && versions.Keys.FirstOrDefault(v => v.Equals(parts[1], StringComparison.OrdinalIgnoreCase)) is { } ver)
                Send("application/octet-stream", versions[ver]);
            else NotFound();
        }
        else if (path == "/v2/FindPackagesById()")
        {
            var id = url.Query.Split('&').Select(p => p.TrimStart('?')).First(p => p.StartsWith("id=")).Substring(3).Trim('\'');
            id = Uri.UnescapeDataString(id).Trim('\'');
            int skip = 0;
            foreach (var q in url.Query.TrimStart('?').Split('&')) if (q.StartsWith("skip=")) skip = int.Parse(q[5..]);
            if (!Find(id, out var versions)) { Send("application/atom+xml", TestFiles.Bytes(Feed(new List<string>(), null))); return; }
            var all = versions.Keys.ToList();
            var page = all.Skip(skip).Take(V2PageSize).ToList();
            string? next = skip + V2PageSize < all.Count ? $"{BaseUrl}v2/FindPackagesById()?id='{id}'&skip={skip + V2PageSize}" : null;
            Send("application/atom+xml", TestFiles.Bytes(Feed(page, next)));
        }
        else if (path.StartsWith("/v2/package/"))
        {
            var parts = path["/v2/package/".Length..].Split('/');
            if (parts.Length == 2 && Find(parts[0], out var versions) && versions.TryGetValue(parts[1], out var bytes)) Send("application/octet-stream", bytes);
            else NotFound();
        }
        else NotFound();
    }

    private bool Find(string id, out SortedDictionary<string, byte[]> versions)
    {
        lock (_lock) return _packages.TryGetValue(id, out versions!);
    }

    private static string Feed(IEnumerable<string> versions, string? next)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="utf-8"?><feed xml:base="x" xmlns="http://www.w3.org/2005/Atom" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata"><title/>""");
        foreach (var v in versions) sb.Append($"<entry><id>x</id><m:properties><d:Version>{v}</d:Version></m:properties></entry>");
        if (next is not null) sb.Append($"<link rel=\"next\" href=\"{System.Security.SecurityElement.Escape(next)}\" />");
        sb.Append("</feed>");
        return sb.ToString();
    }

    public void Dispose()
    {
        _listener.Close();
        try { _loop.Wait(2000); } catch { }
    }
}

