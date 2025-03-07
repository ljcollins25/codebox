using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Text;
using System.Text.Json;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;

namespace AzureDevops.Pipeline.Utilities;

public class WriteOperation(IConsole Console) : TaskOperationBase(Console)
{
    public string? DisplayName;

    public required Guid TargetId;

    public required FileInfo Source;

    protected override async Task<int> RunCoreAsync()
    {
        var record = new TimelineRecord()
        {
            Id = ComputeRecordId(DisplayName) ?? TargetId,
            Name = DisplayName,
            ParentId = DisplayName == null ? null : TargetId
        };

        await UpdateTimelineRecordAsync(record);

        return 0;
    }

    private Guid? ComputeRecordId(string? displayName)
    {
        if (displayName == null) return null;


        throw new NotImplementedException();
    }
}
