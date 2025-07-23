using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;

namespace AzureDevops.Pipeline.Utilities;

public class PreviewYaml(IConsole Console) : TaskOperationBase(Console)
{
    public string? OutputPath;

    protected override async Task<int> RunCoreAsync()
    {
        var response = await HttpClient.PostAsync(
            $"{definition.Project.Name}/_apis/pipelines/{definition.Id}/preview?api-version=7.1",
            JsonContent.Create(new PreviewParameters()));

        var content = await response.Content.ReadAsStringAsync();

        //var record = GetAncestorsAndSelf(taskInfo.TaskId).FirstOrDefault(r => r.RecordType == "Phase");

        return 0;
    }

    public class PreviewParameters
    {
        public bool previewRun { get; set; } = true;
    }
}
