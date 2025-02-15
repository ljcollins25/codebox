using System;
using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;

namespace AzureDevops.Pipeline.Utilities;

public class CopyLogOperation(IConsole Console) : TaskOperationBase(Console)
{
    public Guid? PhaseId;

    public string? TargetId;

    public string? ParentJobName;

    public string? SourceId;

    public required string Name;

    public bool Complete = true;

    public int? Order;

    public int? StartLine;

    public int? EndLine;

    protected override async Task<int> RunCoreAsync()
    {
        PhaseId ??= await GetAncestorsAndSelfAsync(taskInfo.TaskId)
            .ThenAsync(s => s.First(r => r.RecordType == nameof(RecordTypes.Phase)).Id);

        var targetId = GetId(TargetId) ?? GetId(Name).Value;
        var sourceId = GetId(SourceId) ?? taskInfo.TaskId;

        Guid? parentId = null;

        var record = await TryGetRecordAsync(targetId);

        if (record == null)
        {
            parentId = GetId(ParentJobName) ?? taskInfo.JobId;

            if (ParentJobName.IsNonEmpty())
            {
                await UpdateTimelineRecordAsync(new()
                {
                    Id = parentId.Value,
                    ParentId = PhaseId,
                    Name = ParentJobName,
                    RecordType = "Job",
                    Result = TaskResult.Succeeded,
                    State = TimelineRecordState.Completed,
                    Order = 0
                });

                Helpers.GetSetPipelineVariableText("AZPUTILS_OUT_PARENT_JOB_ID", parentId.Value.ToString(), emit: true, log: true);
            }
        }

        Helpers.GetSetPipelineVariableText("AZPUTILS_OUT_TARGET_ID", targetId.ToString(), emit: true, log: true);

        var sourceRecord = await GetRecordAsync(sourceId);

        TaskLogReference? log = default;
        if (StartLine == null && EndLine == null)
        {
            log = sourceRecord.Log;
        }

        record ??= await UpdateTimelineRecordAsync(new()
        {
            Id = targetId,
            Name = Name,
            ParentId = parentId,
            RecordType = "Task",
            Log = log,
            Order = Order,
            Attempt = sourceRecord.Attempt
        });

        if (log == null)
        {
            var logLines = await GetLogLinesAsync(sourceRecord);
            var stream = new MemoryStream();
            using (var writer = new StreamWriter(stream, leaveOpen: true))
            {
                foreach (var line in logLines)
                {
                    writer.WriteLine(line);
                }
            }

            await AppendLogContentAsync(record, stream);
        }

        if (Complete)
        {
            record = await UpdateTimelineRecordAsync(new()
            {
                Id = targetId,
                Log = log,
                Order = Order,
                State = TimelineRecordState.Completed,
                StartTime = sourceRecord.StartTime,
                FinishTime = sourceRecord.FinishTime ?? DateTime.UtcNow,
                Result = sourceRecord.Result ?? TaskResult.Succeeded
            });
        }

        return 0;
    }
}
