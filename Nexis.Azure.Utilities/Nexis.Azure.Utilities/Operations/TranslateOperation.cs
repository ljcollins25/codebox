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
using System.Net.Http.Json;
using System.Reflection.Metadata;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using CliWrap;
using DotNext.IO;
using Microsoft.Playwright;

namespace Nexis.Azure.Utilities;

public record class TranslateOperation(IConsole Console, CancellationToken token) : BrowserOperationBase(Console, token)
{
    public bool Legacy = false;

    public required IReadOnlyList<LanguageCode> Languages { get; set; } = [eng, jpn, kor, zho];

    public required string AudioFile;

    public string? GdrivePath;

    public bool DryRun = false;

    public bool ApiMode = true;

    public override async Task<int> RunAsync(IPlaywright playwright, IBrowser browser, IPage page)
    {
        //var page = await browser.NewPageAsync();

        if (!string.IsNullOrEmpty(GdrivePath))
        {
            await ExecAsync("rclone", ["copyto", AudioFile, GdrivePath]);

            var link = new StringBuilder();

            await ExecAsync("rclone", ["link", GdrivePath], PipeTarget.ToStringBuilder(link));

            var fileName = Uri.EscapeDataString(Path.GetFileName(AudioFile));
            AudioFile = link.ToString().Trim().Replace("open?id=", "file/d/") + "/view?filename=" + fileName;

            if (ApiMode)
            {
                var request = await page.GotoAndGetUrlRequest(
                    "https://app.heygen.com/projects?create_video_modal=true&index&modal_screen=translate_new",
                    "https://api2.heygen.com/v1/pacific/account.get");

                var client = await request.AsHttpClientAsync();

                var response = await client.PostApiRequestAsync(new TranslateRequest(
                    name: fileName,
                    google_url: AudioFile,
                    output_languages: Languages,
                    keep_the_same_format: false),
                token);

                response.EnsureSuccessStatusCode();

                var responseBody = await response.Content.ReadAsStringAsync();

                return 0;
            }
        }

        if (Legacy)
        {
            await page.GotoAsync("https://app.heygen.com/projects");

            await page.GetByText("Create video").ClickAsync();

            await page.GetByText("Translate a video").ClickAsync();
        }
        else
        {
            await page.GotoAsync("https://app.heygen.com/projects?create_video_modal=true&index&modal_screen=translate_new");
        }

        if (AudioFile.StartsWith("http"))
        {
            var videoInput = page.GetByPlaceholder("Paste your video");
            await videoInput.FillAsync(AudioFile);

            await videoInput.PressAsync("Enter");

            await page.GetByText("Next").ClickAsync();
        }
        else
        {
            var fileChooser = await page.RunAndWaitForFileChooserAsync(async () =>
            {
                //await page.GetByText("Drag and drop video here").ClickAsync();
                await page.GetByText("Browse Local Files").ClickAsync();
            });

            await fileChooser.SetFilesAsync(Path.GetFullPath(AudioFile));
        }

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

        if (!DryRun)
        {
            await page.GetByText("Translate", new() { Exact = true }).ClickAsync();
        }

        if (!string.IsNullOrEmpty(GdrivePath))
        {
            await Task.Delay(2000);
        }

        return 0;
    }
}