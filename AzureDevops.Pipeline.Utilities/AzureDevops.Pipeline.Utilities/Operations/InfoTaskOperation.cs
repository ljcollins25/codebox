using System;
using System.Collections.Generic;
using System.CommandLine;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;

namespace AzureDevops.Pipeline.Utilities;

public class DeletePipelineRun(IConsole Console) : TaskOperationBase(Console)
{
    public required int RunId;

    public string? Project = null;

    protected override async Task<int> RunCoreAsync()
    {
        Project ??= this.adoBuildUri.Project;

        await client.DeleteBuildAsync(Project, RunId);
        
        return 0;
    }
}
