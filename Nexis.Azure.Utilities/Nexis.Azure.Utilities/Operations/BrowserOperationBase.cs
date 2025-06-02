using System.CommandLine;
using Microsoft.Playwright;

namespace Nexis.Azure.Utilities;

public abstract record class BrowserOperationBase(IConsole Console, CancellationToken token)
{
    private static readonly SemaphoreSlim BrowserSemaphore = CreateMutex();

    public async Task<int> RunAsync()
    {
        using var _ = await BrowserSemaphore.AcquireAsync(token);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.ConnectOverCDPAsync("http://localhost:19222");
        var page = browser.Contexts.First().Pages.First();

        return await RunAsync(playwright, browser, page);
    }

    public abstract Task<int> RunAsync(IPlaywright playwright, IBrowser browser, IPage page);
}