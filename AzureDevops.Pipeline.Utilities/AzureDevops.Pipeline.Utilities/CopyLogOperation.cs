using System;
using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;

namespace AzureDevops.Pipeline.Utilities;

public class CopyLogOperation(IConsole Console) : LogOperationBase(Console)
{
    public Guid? PhaseId;

    public string? TargetId;

    public string? ParentJobName;

    public string? SourceId;

    public required string Name;

    public bool CopyState = false;

    public int? Order;

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

        TimelineRecord sourceRecord = await GetRecordAsync(sourceId)!;

        TaskLogReference? log = default;


        Console.WriteLine($"Source Log {sourceRecord.Log?.Id} for {sourceRecord.Id}:{sourceRecord.Name}");

        if (!NeedsPreprocessing && sourceRecord.Log != null)
        {
            log = sourceRecord.Log;
        }

        if (CopyState)
        {
            record = await UpdateTimelineRecordAsync(new()
            {
                Id = targetId,
                Name = Name,
                ParentId = parentId,
                RecordType = "Task",
                Log = log,
                Order = Order,
                Attempt = sourceRecord.Attempt,
                State = sourceRecord.State,
                StartTime = sourceRecord.StartTime,
                FinishTime = sourceRecord.FinishTime,
                Result = sourceRecord.Result
            });

            if (log != null)
            {
                Console.WriteLine($"Copied log {record.Log?.Id} to {record.Id}:{record.Name}. ");
            }
        }
        else
        {
            record ??= await UpdateTimelineRecordAsync(new()
            {
                Id = targetId,
                Name = Name,
                ParentId = parentId,
                RecordType = "Task",
                Log = log
            });
        }

        if (NeedsPreprocessing)
        {
            if (record.Log == null)
            {
                record.Log = await CreateLogAsync(record);
                record = await UpdateTimelineRecordAsync(new()
                {
                    Id = record.Id,
                    Log = record.Log
                });
            }

            var logLines = await GetProcessedLogLinesAsync(sourceRecord);

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

        return 0;
    }
}
