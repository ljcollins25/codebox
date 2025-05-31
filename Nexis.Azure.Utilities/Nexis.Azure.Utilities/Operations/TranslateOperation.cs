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

public record class TranslateOperation(IConsole Console, CancellationToken token)
{
    public required List<LanguageCode> Languages { get; set; } = [eng, jpn, kor, zho];

    public required string AudioFile;

    public async Task<int> RunAsync()
    {
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.ConnectOverCDPAsync("http://localhost:19222");

        var page = browser.Contexts.First().Pages.FirstOrDefault();

        //var page = await browser.NewPageAsync();
        await page.GotoAsync("https://app.heygen.com/projects");

        await page.GetByText("Create video").ClickAsync();

        await page.GetByText("Translate a video").ClickAsync();

        var fileChooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.GetByText("Drag and drop video here").ClickAsync();
        });

        await fileChooser.SetFilesAsync(AudioFile);

        await page.GetByText("Advanced", new() { Exact = true }).ClickAsync();

        await page.GetByText("Allow dynamic duration").ClickAsync();

        await page.Keyboard.PressAsync("Shift+Tab");

        await page.Keyboard.PressAsync("Shift+Tab");


        //var languagePicker = await page.QuerySelectorAsync("input[placeholder*='Choose language']");

        foreach (var language in Languages)
        {
            await page.Keyboard.InsertTextAsync(language.ToLanguageOption());

            await Task.Delay(1000);

            await page.Keyboard.PressAsync("Enter");

            await Task.Delay(1000);
        }

        await page.Keyboard.PressAsync("Shift+Tab");
        // Disable dynamic duration

        await page.GetByText("Translate audio only").ClickAsync();

        await page.GetByText("Enable captions").ClickAsync();

        await page.GetByText("Translate", new() { Exact = true }).ClickAsync();

        return 0;
    }
}