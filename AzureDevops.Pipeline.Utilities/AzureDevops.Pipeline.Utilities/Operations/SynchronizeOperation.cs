using System.CommandLine;
using System.CommandLine.IO;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.TestManagement.TestPlanning.WebApi;

namespace AzureDevops.Pipeline.Utilities;

public class SynchronizeOperation(IConsole Console, CancellationToken token) : TaskOperationBase(Console)
{
    public Guid RecordId;
    public required int JobCount;
    public required string Qualifier;

    public bool WaitOnly = false;

    public bool SetComplete = false;

    public string? Timeout = null;

    public string? SynchronizationIdPropertyKey = null;

    /// <summary>
    /// Synchronize at task-level rather than phase level
    /// </summary>
    public Scopes Scope = Scopes.Default;

    public string DisplayName = Environment.MachineName;

    public string SyncParticipantTimestampVar = $"SyncParticipantTimestamp";
    public string SyncParticipantVarPrefix = $"SyncParticipant.";

    public enum Scopes
    {
        Default,
        Task,
    }


    protected override async Task<int> RunCoreAsync()
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (Timeout != null)
        {
            cts.CancelAfter(TimeSpanSetting.Parse(Timeout));
        }

        var guid = Guid.NewGuid();
        var prefix = $"{SyncParticipantVarPrefix}{Qualifier}";


        if (SynchronizationIdPropertyKey != null)
        {
            var properties = await GetBuildProperties();
            if (properties.TryGetValue(SynchronizationIdPropertyKey, out var value))
            {
                RecordId = Guid.Parse(value.ToString()!);
            }
            else
            {
                Console.Error.WriteLine($"Unable to resolve SynchronizationIdPropertyKey: '{SynchronizationIdPropertyKey}'");
                return -1;
            }
        }
        else if (Scope == Scopes.Task)
        {
            RecordId = taskInfo.TaskId;
        }
        else if (RecordId != default)
        {
        }
        else
        {
            await RefreshTimelineRecordsAsync();
            var id = taskInfo.JobId;
            while (RecordsById.TryGetValue(id, out var parentRecord))
            {
                if (Enum.TryParse<RecordTypes>(parentRecord.RecordType, ignoreCase: true, out var recordType)
                    && recordType == RecordTypes.Phase)
                {
                    RecordId = parentRecord.Id;
                }

                if (id == parentRecord.Id) break;

                id = parentRecord.Id;
            }
        }

        if (RecordId == default)
        {
            Console.Error.WriteLine("Unable to resolve PhaseId");
            return -1;
        }

        var record = new TimelineRecord()
        {
            Id = RecordId,
            ParentId = (await TryGetRecordAsync(RecordId))?.ParentId,
            Variables =
                {
                    [$"{prefix}{guid}"] = new VariableValue(DisplayName, false)
                }
        };

        if (WaitOnly)
        {
            record.Variables.Clear();
        }

        async Task setTaskResult(TaskResult result)
        {
            if (!SetComplete)
            {
                return;
            }

            if (record.Result == null)
            {
                Console.WriteLine($"Setting result to {result}");
                await RaisePlanEventAsync(GetTaskCompletedEvent(result, record));
            }
            else
            {
                Console.WriteLine($"Skipping due to exit attempted result: {result}, actual result: {record.Result}");
            }
        }

        try
        {
            for (; ; await Task.Delay(TimeSpan.FromSeconds(PollSeconds), cts.Token))
            {
                var updatedRecord = await UpdateTimelineRecordAsync(record);
                record.Variables.Clear();

                var participants = updatedRecord.Variables.Where(k => k.Key.StartsWith(prefix)).ToList();

                Console.WriteLine($"Job: '{DisplayName}', Participants: {participants.Count}, RequiredParticipants: {JobCount}");

                if (participants.Count >= JobCount)
                {
                    await setTaskResult(TaskResult.Succeeded);
                    return 0;
                }
            }
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException || ex is TaskCanceledException)
            {
                await setTaskResult(TaskResult.Canceled);
            }

            return -2;
        }
    }

    private record ParticipantEntry(string AgentName, DateTime TimeStamp);
}
