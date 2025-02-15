using System;
using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;

namespace AzureDevops.Pipeline.Utilities;

public class DownloadLogsOperation(IConsole Console) : TaskOperationBase(Console)
{
    public string? Output;

    public string? SourceId;

    public int? StartLine;

    public int? EndLine;

    protected override async Task<int> RunCoreAsync()
    {
        var sourceId = GetId(SourceId) ?? taskInfo.TaskId;
        var sourceRecord = await GetRecordAsync(sourceId);

        var log = sourceRecord.Log;

        var logUri = $"{adoBuildUri.OrganizationUri}{build.Project.Id}/_apis/build/builds/{build.Id}/logs/{log.Id}?startLine={StartLine}&endLine={EndLine}";

        var logResponse = await HttpClient.GetAsync(logUri);

        logResponse.EnsureSuccessStatusCode();

        if (Output == null)
        {
            using var s = await logResponse.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(s);

            while (Out.Var(out var line, await reader.ReadLineAsync()) != null)
            {
                Console.WriteLine(line!);
            }
        }
        else
        {
            using (var fs = File.Open(Output, FileMode.Create, FileAccess.ReadWrite))
            {
                await logResponse.Content.CopyToAsync(fs);
            }
        }

        return 0;
    }
}
