using System.CommandLine;
using System.Diagnostics;
using Microsoft.Playwright;

namespace Nexis.Azure.Utilities;

public abstract record class BrowserOperationBase(IConsole Console, CancellationToken token)
{
    public static readonly AsyncLocal<string?> BrowserProcessPath = new();
    public static readonly AsyncLocal<int?> BrowserProcessPort = new();

    /// <summary>
    /// Browser name (brave, chrome, edge, chromium) or full path to the browser executable.
    /// </summary>
    public string? Browser;

    /// <summary>
    /// When true, forces launching a new browser instance even if one is already running.
    /// </summary>
    public bool ForceNewInstance;

    public static int DebuggingPort => BrowserProcessPort.Value ?? (BrowserProcessPath.Value != null ? 19221 : 19222);

    private static readonly SemaphoreSlim BrowserSemaphore = CreateMutex();

    private static readonly Dictionary<string, string[]> WellKnownBrowserPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] =
        [
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"),
        ],
        ["brave"] =
        [
            @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe",
            @"C:\Program Files (x86)\BraveSoftware\Brave-Browser\Application\brave.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"BraveSoftware\Brave-Browser\Application\brave.exe"),
        ],
        ["edge"] =
        [
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        ],
        ["chromium"] =
        [
            @"C:\Program Files\Chromium\Application\chrome.exe",
        ],
    };

    /// <summary>
    /// Resolves a browser name or path to a full executable path.
    /// </summary>
    public static string ResolveBrowserPath(string? browser)
    {
        if (string.IsNullOrEmpty(browser))
        {
            return BrowserProcessPath.Value ?? @"C:\Program Files\Chromium\Application\chrome.exe";
        }

        // If it looks like a path (contains separator or file extension), use it directly
        if (browser.Contains(Path.DirectorySeparatorChar) || browser.Contains(Path.AltDirectorySeparatorChar) || browser.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return browser;
        }

        // Look up well-known browser names
        if (WellKnownBrowserPaths.TryGetValue(browser, out var candidates))
        {
            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            // Return the first candidate even if not found, so the error message is clear
            return candidates[0];
        }

        // Treat as a direct path
        return browser;
    }

    public async Task<int> RunAsync()
    {
        using var _ = await BrowserSemaphore.AcquireAsync(token);
        var resolvedPath = ResolveBrowserPath(Browser);
        await EnsureChromiumRunning(resolvedPath, ForceNewInstance);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.ConnectOverCDPAsync($"http://localhost:{DebuggingPort}");
        var page = browser.Contexts.First().Pages.First();

        return await RunAsync(playwright, browser, page);
    }

    public abstract Task<int> RunAsync(IPlaywright playwright, IBrowser browser, IPage page);

    public static async Task EnsureChromiumRunning(string? processPath = null, bool forceNew = false)
    {
        processPath ??= BrowserProcessPath.Value ?? @"C:\Program Files\Chromium\Application\chrome.exe";
        processPath = Path.GetFullPath(processPath);
        var name = Path.GetFileNameWithoutExtension(processPath);
        var existingProcess = Process.GetProcessesByName(name).FirstOrDefault(p => string.Equals(p.MainModule!.FileName, processPath));

        if (existingProcess == null || forceNew)
        {
            var _ = ExecAsync(processPath, [$"--remote-debugging-port={DebuggingPort}"]).Status;

            await Task.Delay(2000);
        }

    }
}