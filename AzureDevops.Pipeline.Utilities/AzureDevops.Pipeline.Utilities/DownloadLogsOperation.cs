using System;
using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;

namespace AzureDevops.Pipeline.Utilities;

public class DownloadLogsOperation(IConsole Console) : LogOperationBase(Console)
{
    public string? Target;

    public string? SourceId;

    protected override async Task<int> RunCoreAsync()
    {
        var sourceId = GetId(SourceId) ?? taskInfo.TaskId;
        var sourceRecord = await GetRecordAsync(sourceId);

        var log = sourceRecord.Log;

        var logUri = $"{adoBuildUri.OrganizationUri}{build.Project.Id}/_apis/build/builds/{build.Id}/logs/{log.Id}?startLine={StartLine}&endLine={EndLine}";

        var logResponse = await HttpClient.GetAsync(logUri);

        logResponse.EnsureSuccessStatusCode();

        if (Target == null)
        {
            if (NeedsPreprocessing)
            {
                var lines = await GetProcessedLogLinesAsync(sourceRecord);
                foreach (var line in lines)
                {
                    Console.WriteLine(line!);
                }
            }
            else
            {
                using var s = await logResponse.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(s);

                while (Out.Var(out var line, await reader.ReadLineAsync()) != null)
                {
                    Console.WriteLine(line!);
                }
            }
        }
        else
        {
            using (var fs = File.Open(Target, FileMode.Create, FileAccess.ReadWrite))
            {
                if (NeedsPreprocessing)
                {
                    var lines = await GetProcessedLogLinesAsync(sourceRecord);
                    using var writer = new StreamWriter(fs);
                    foreach (var line in lines)
                    {
                        line!.WriteLine(writer);
                    }
                }
                else
                {
                    await logResponse.Content.CopyToAsync(fs);
                }
            }
        }

        return 0;
    }
}
