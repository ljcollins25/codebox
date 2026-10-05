using Xunit;

namespace R2Pipe.Tests;

public class MiscTests
{
    [Theory]
    [InlineData("32M", 32L << 20)] [InlineData("16MiB", 16L << 20)] [InlineData("1g", 1L << 30)] [InlineData("64k", 64L << 10)] [InlineData("1000", 1000)]
    public void Sizes_parse(string s, long expected) => Assert.Equal(expected, Sizes.Parse(s));

    [Fact]
    public void Sizes_reject_garbage() => Assert.Throws<UserError>(() => Sizes.Parse("lots"));

    [Fact]
    public void Args_parse_options_flags_and_positionals()
    {
        var a = new Args(new[] { "file.bin", "--name", "x", "--parallel=8", "-o", "-", "--quiet" });
        Assert.Equal(new[] { "file.bin" }, a.Positional);
        Assert.Equal("x", a.Get("name")); Assert.Equal(8, a.GetInt("parallel", 4));
        Assert.Equal("-", a.Get("o")); Assert.True(a.Has("quiet"));
        Assert.Equal(4, new Args(Array.Empty<string>()).GetInt("parallel", 4));
        Assert.Throws<UserError>(() => new Args(new[] { "--name" }));
        Assert.Equal(new[] { "-" }, new Args(new[] { "-" }).Positional);
    }

    private static string TempFile() => Path.Combine(Path.GetTempPath(), "r2pipe-secrets-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void Secret_store_round_trips_and_deletes()
    {
        var p = TempFile();
        try
        {
            var st = new SecretStore(p, useDpapi: false);
            Assert.Null(st.Get("a")); st.Set("a", "1"); st.Set("b", "2");
            Assert.Equal("1", st.Get("a")); st.Delete("a"); Assert.Null(st.Get("a")); Assert.Equal("2", st.Get("b"));
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void Secret_store_uses_DPAPI_on_Windows_only_and_does_not_keep_plain_text_there()
    {
        if (!OperatingSystem.IsWindows()) return;
        var p = TempFile();
        try
        {
            var st = new SecretStore(p);
            st.Set("k", "very-secret-value");
            Assert.DoesNotContain("very-secret-value", File.ReadAllText(p));
            Assert.Equal("very-secret-value", st.Get("k"));
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void Credentials_prefer_env_then_store_and_need_both_halves()
    {
        var p = TempFile();
        try
        {
            var st = new SecretStore(p, useDpapi: false);
            st.Set(Credentials.IdKey, "stored-id"); st.Set(Credentials.SecretKey, "stored-secret");
            var fromStore = new Credentials(_ => null, st).Headers();
            Assert.Equal("stored-id", fromStore["CF-Access-Client-Id"]);
            var env = new Dictionary<string, string> { ["CF_ACCESS_CLIENT_ID"] = "env-id", ["CF_ACCESS_CLIENT_SECRET"] = "env-secret" };
            var fromEnv = new Credentials(k => env.GetValueOrDefault(k), st).Headers();
            Assert.Equal("env-id", fromEnv["CF-Access-Client-Id"]); Assert.Equal("env-secret", fromEnv["CF-Access-Client-Secret"]);
            Assert.Throws<UserError>(() => new Credentials(k => k == "CF_ACCESS_CLIENT_ID" ? "only-id" : null, new SecretStore(TempFile(), false)).Headers());
            Assert.Throws<UserError>(() => new Credentials(_ => null, new SecretStore(TempFile(), false)).Headers());
            Assert.Equal("https://pipe.ref12.dev", new Credentials(_ => null, new SecretStore(TempFile(), false)).Url);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void Access_headers_go_to_the_worker_but_not_to_r2()
    {
        // the data URL from the presign is on another host: HttpPipeApi must not send the service token there
        var seen = new List<(string url, bool hasAuth)>();
        var handler = new RecordingHandler(seen);
        var api = new HttpPipeApi("https://pipe.example.invalid", new Dictionary<string, string> { ["CF-Access-Client-Secret"] = "s" }, new HttpClient(handler));
        api.PutAsync(new UrlResult("https://acct.r2.cloudflarestorage.com/r2pipe/t/x/000001?X-Amz-Signature=abc", "PUT"), new byte[3], 3, default).GetAwaiter().GetResult();
        api.PutAsync(new UrlResult("https://pipe.example.invalid/t/x/parts/1/data", "PUT", ViaWorker: true), new byte[3], 3, default).GetAwaiter().GetResult();
        Assert.False(seen[0].hasAuth); Assert.True(seen[1].hasAuth);
    }

    private sealed class RecordingHandler(List<(string, bool)> seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            seen.Add((r.RequestUri!.ToString(), r.Headers.Contains("CF-Access-Client-Secret")));
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    [Fact]
    public async Task Retry_stops_on_final_errors_and_counts_attempts()
    {
        int calls = 0;
        await Assert.ThrowsAsync<ApiException>(() => Retry.RunAsync<int>(5, _ => { calls++; throw new ApiException(System.Net.HttpStatusCode.Gone, "410"); }, Util.NoDelay, default));
        Assert.Equal(1, calls);
        calls = 0;
        await Assert.ThrowsAsync<IOException>(() => Retry.RunAsync<int>(3, _ => { calls++; throw new IOException("x"); }, Util.NoDelay, default));
        Assert.Equal(3, calls);
        calls = 0;
        Assert.Equal(7, await Retry.RunAsync(3, a => { calls++; return a < 3 ? throw new ApiException(System.Net.HttpStatusCode.BadGateway, "502") : Task.FromResult(7); }, Util.NoDelay, default));
        Assert.Equal(3, calls);
    }
}
