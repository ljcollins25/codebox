using System;
using System.Collections.Generic;
using System.CommandLine;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;

namespace AzureDevops.Pipeline.Utilities;

public class SetVariableOperation(IConsole Console) : TaskOperationBase(Console)
{
    public string? TargetId;

    protected override async Task<int> RunCoreAsync()
    {
        var targetId = GetId(TargetId) ?? taskInfo.TaskId;


        Helpers.GetSetPipelineVariableText("AZPUTILS_OUT_TASK_URL", TaskUrl, emit: true, log: true);

        await RefreshTimelineRecordsAsync();

        var record = RecordsById.Values.First(r => r.Name?.Contains("(Download") == true);
        //var record = GetAncestorsAndSelf(taskInfo.TaskId).FirstOrDefault(r => r.RecordType == "Phase");
        
        return 0;
    }
}
