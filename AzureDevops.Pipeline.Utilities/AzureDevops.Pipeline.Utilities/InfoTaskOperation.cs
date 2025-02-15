using System;
using System.Collections.Generic;
using System.CommandLine;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;

namespace AzureDevops.Pipeline.Utilities;

public class InfoTaskOperation(IConsole Console) : TaskOperationBase(Console)
{
    protected override async Task<int> RunCoreAsync()
    {
        await RefreshTimelineRecordsAsync();
        //var record = GetAncestorsAndSelf(taskInfo.TaskId).FirstOrDefault(r => r.RecordType == "Phase");
        
        return 0;
    }
}
