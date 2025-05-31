using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.CommandLine;
using System.CommandLine.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.Metadata;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using DotNext.IO;
using Microsoft.Playwright;

namespace Nexis.Azure.Utilities;

public record class DownloadTranslation(IConsole Console, CancellationToken token)
{
    public required LanguageCode Language;

    public required string VideoId;

    public required string TargetFolder;

    public async Task<int> RunAsync()
    {
        Directory.CreateDirectory(TargetFolder);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.ConnectOverCDPAsync("http://localhost:19222");

        var page = browser.Contexts.First().Pages.FirstOrDefault();

        //var page = await browser.NewPageAsync();
        await page.GotoAsync($"https://app.heygen.com/videos/{VideoId}?index");

        await Task.Delay(1000);

        await page.GetByTitle("Download").ClickAsync();

        await Task.Delay(1000);

        foreach (var type in new[] { FileType.mp4, FileType.ass })
        {
            if (type == FileType.ass)
            {
                await page.GetByRole(AriaRole.Button, new() { Name = "Captions" }).ClickAsync();
            }

            var chooser = await page.RunAndWaitForDownloadAsync(async () =>
            {
                await page.GetByText("Download", new() { Exact = true }).Nth(1).ClickAsync();
            });

            await chooser.SaveAsAsync(Path.Combine(TargetFolder, $"{Language}.audio.{type}"));
        }

        return 0;
    }
}