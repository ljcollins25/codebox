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
using System.Text.Json;
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
using static Nexis.Azure.Utilities.TransformFiles;

namespace Nexis.Azure.Utilities;

public record class ListVideosOperation(IConsole Console, CancellationToken token) : BrowserOperationBase(Console, token)
{
    public bool Organize = false;

    public bool DryRun = true;

    public int PageLimit = 100;

    public List<VideoDataItem> Results = new List<VideoDataItem>();

    public Dictionary<VideoStatus, string> StatusFolders = new()
    {
        [VideoStatus.waiting] = "c77db7d371e747cf93225e28d60e9de3",
        [VideoStatus.completed] = "d90fd9fe544f4109bdbc3cc167451217",
        [VideoStatus.processing] = "14bb07cda9ff4dbfaed238fb0fbd8c65",
    };

    public string? MarkerFolder;

    public bool Print = false;

    public override async Task<int> RunAsync(IPlaywright playwright, IBrowser browser, IPage page)
    {
        var request = await page.GotoAndGetUrlRequest(
            "https://app.heygen.com/projects",
            "https://api2.heygen.com/v1/pacific/account.get");

        var client = await request.AsHttpClientAsync();

        string additionalQuery = "";
        while (!token.IsCancellationRequested)
        {
            var url = ListDataResponse.Url(PageLimit) + additionalQuery;
            var response = await client.GetFromJsonAsync<ListDataResponse>(url, token);
            if (response != null)
            {
                Results.AddRange(response.data.list);
            }

            if (string.IsNullOrEmpty(response?.data.token)) break;

            additionalQuery = $"&token={Uri.EscapeDataString(response.data.token)}";
        }

        if (!string.IsNullOrEmpty(MarkerFolder))
        {
            var resultsById = Results.ToLookup(r => r.GetInfo().Id);
            try
            {
                var markerFiles = Directory.GetFiles(MarkerFolder, "*.marker", SearchOption.AllDirectories);
                foreach (var file in markerFiles)
                {
                    var data = JsonSerializer.Deserialize<MarkerData>(File.ReadAllText(file));
                    foreach (var result in resultsById[data.Id])
                    {
                        var info = result.GetInfo();
                        if (!info.Id.Value.Contains("_"))
                        {
                            info.Id = Vuid.FromFileName(Path.GetFileNameWithoutExtension(file), includeGuid: false);
                            result.displayInfo = info;
                        }
                    }
                }
            }
            catch
            {

            }
        }

        if (Organize)
        {
            var resultsByStatus = Results.ToLookup(r => r.status);
            foreach (var (status, folder) in StatusFolders)
            {
                foreach (var result in resultsByStatus[status])
                {
                    if (result.project_id != folder)
                    {
                        var record = TranslationRecord.FromVideoItem(result);
                        var fileName = Uri.EscapeDataString($"{result.title}--{record.FileName}");
                        Console.Out.WriteLine($"Moving to  ({status}){folder}/{fileName} from {result.project_id ?? "<null>"}/{result.name}");

                        if (!DryRun)
                        {
                            await client.PostApiRequestAsync(new MoveRequest(folder, result.id), token);

                            await client.PostApiRequestAsync(new UpdateRequest(result.id, new(fileName)), token);
                        }
                    }
                }
            }
        }

        if (Print)
        {
            var table = new DisplayTable();
            table.AddColumn(nameof(VideoDataItem.id));
            table.AddColumn(nameof(VideoDataItem.title));
            table.AddColumn(nameof(VideoDataItem.status));
            table.AddColumn(nameof(VideoDataItem.creator_name));
            table.AddColumn(nameof(VideoDataItem.output_language));
            foreach (var item in Results.OrderBy(i => (i.displayInfo ?? i.GetInfo()).Id.Value ?? "")
                .ThenBy(i => i.output_language)
                .ThenBy(i => i.GetInfo().Index))
            {
                table.Add(item);
            }

            table.Write(line => Console.Out.WriteLine(line.ToString()));
        }
        //var page = await browser.NewPageAsync();

        return 0;
    }
}