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
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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

public record class DownloadTranslation(IConsole Console, CancellationToken token) : BrowserOperationBase(Console, token)
{
    public required LanguageCode Language;

    public required string VideoId;

    public required string TargetFolder;

    public string BaseName = "";

    public bool Delete = false;

    public bool Download = true;

    public string? CompletedFolderId = null;

    public bool ApiMode = true;

    public string SubFile => TargetFile(FileType.srt);
    public string VideoFile => TargetFile(FileType.mp4);

    public string TargetFile(FileType type) => Path.Combine(TargetFolder, $"{BaseName}{Language}.audio.{type}");

    public override async Task<int> RunAsync(IPlaywright playwright, IBrowser browser, IPage page)
    {
        Console.WriteLine($"Downloading {VideoId} to {TargetFile(FileType.mp4)}");

        Directory.CreateDirectory(TargetFolder);

        var request = await page.GotoAndGetUrlRequest(
            $"https://app.heygen.com/videos/{VideoId}?index",
            $"https://api2.heygen.com/v1/pacific/collaboration/video.details?item_id={VideoId}");

        //var headers = await request.AllHeadersAsync();
        if (ApiMode)
        {
            var message = await request.AsHttpRequestAsync();
            var client = await request.AsHttpClientAsync();

            var result = await client.SendAsync(message);
            var details = await result.Content.ReadFromJsonAsync<VideoDetails>();
            var data = details!.data;

            if (Download)
            {
                foreach (var type in new[] { FileType.mp4, FileType.ass })
                {
                    var url = type == FileType.mp4 ? data.video_url : data.caption_url;

                    var response = await client.GetAsync(url);

                    response.EnsureSuccessStatusCode();

                    using (var fs = File.Create(TargetFile(type)))
                    {
                        await response.Content.CopyToAsync(fs, token);
                    }
                }
            }

            if (Delete)
            {
                var response = await client.PostApiRequestAsync(
                    new DeleteRequest()
                    {
                        items = [
                            new() { id = VideoId }
                        ]
                    },
                    token);
            }
            else if (!string.IsNullOrEmpty(CompletedFolderId))
            {
                var response = await client.PostApiRequestAsync(
                    new MoveRequest(
                        item_id: VideoId,
                        project_id: CompletedFolderId
                    ),
                    token);
            }
        }
        else
        {
            //var page = await browser.NewPageAsync();

            await Task.Delay(1000);

            if (Download)
            {
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

                    await chooser.SaveAsAsync(TargetFile(type));
                }
            }

            if (Delete)
            {

                //await page.ClickAsync("[name='more-vertical']");

                //await page.ClickAsync("[name='delete']");

                await page.GotoAsync("https://app.heygen.com/projects?index");

                //await page.PostDataFromBrowserContextAsync(
                //    "https://api2.heygen.com/v1/video_translate/trash",
                //    new DeleteRequest()
                //    {
                //        items = [
                //            new() { id = VideoId }
                //        ]
                //    });
            }
        }

        return 0;
    }

    private void Page_Request(object? sender, IRequest e)
    {
    }

    

}