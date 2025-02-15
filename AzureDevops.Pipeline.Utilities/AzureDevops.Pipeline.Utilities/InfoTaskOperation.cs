using System;
using System.Collections.Generic;
using System.CommandLine;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;

namespace AzureDevops.Pipeline.Utilities;

public class InfoTaskOperation(IConsole Console) : TaskOperationBase(Console)
{
    public bool Load;

    protected override async Task<int> RunCoreAsync()
    {
        Helpers.GetSetPipelineVariableText("AZPUTILS_OUT_TASK_URL", TaskUrl, emit: true, log: true);
        //await RefreshTimelineRecordsAsync();
        //var record = GetAncestorsAndSelf(taskInfo.TaskId).FirstOrDefault(r => r.RecordType == "Phase");
        
        return 0;
    }
}
