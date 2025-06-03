using System.CommandLine;
using System.Diagnostics;
using Microsoft.Playwright;

namespace Nexis.Azure.Utilities;

public abstract record class BrowserOperationBase(IConsole Console, CancellationToken token)
{
    public static readonly AsyncLocal<string?> BrowserProcessPath = new();
    public static readonly AsyncLocal<int?> BrowserProcessPort = new();

    public static int DebuggingPort => BrowserProcessPort.Value ?? (BrowserProcessPath.Value != null ? 19221 : 19222);

    private static readonly SemaphoreSlim BrowserSemaphore = CreateMutex();

    public async Task<int> RunAsync()
    {
        using var _ = await BrowserSemaphore.AcquireAsync(token);
        await EnsureChromiumRunning();

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.ConnectOverCDPAsync($"http://localhost:{DebuggingPort}");
        var page = browser.Contexts.First().Pages.First();

        return await RunAsync(playwright, browser, page);
    }

    public abstract Task<int> RunAsync(IPlaywright playwright, IBrowser browser, IPage page);

    public static async Task EnsureChromiumRunning(string? processPath = null)
    {
        processPath ??= BrowserProcessPath.Value ?? @"C:\Program Files\Chromium\Application\chrome.exe";
        processPath = Path.GetFullPath(processPath);
        var name = Path.GetFileNameWithoutExtension(processPath);
        if (!Process.GetProcessesByName(name).Any(p => string.Equals(p.MainModule!.FileName, processPath)))
        {
            var _ = ExecAsync(processPath, [$"--remote-debugging-port={DebuggingPort}"]).Status;

            await Task.Delay(2000);
        }

    }
}