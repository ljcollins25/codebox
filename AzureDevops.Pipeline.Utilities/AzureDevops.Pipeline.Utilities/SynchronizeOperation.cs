using System.Collections.Generic;
using System.CommandLine;
using System.Text;
using System.Text.Json;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using TimelineRecord = Microsoft.TeamFoundation.DistributedTask.WebApi.TimelineRecord;

namespace AzureDevops.Pipeline.Utilities;

public class SynchronizeOperation(IConsole Console) : TaskOperationBase(Console)
{
    public required Guid PhaseId;
    public required int JobCount;

    public string DisplayName = Environment.MachineName;

    public string ReservationPrefix = $"****reservations:";
    public string SyncParticipantVarPrefix = $"SyncParticipant.";


    protected override async Task<int> RunCoreAsync()
    {
        string allJobsRegisteredKey = $"{PhaseId}";

        try
        {
            var props = await GetBuildProperties();

            if (props.ContainsKey(taskInfo.AllJobsReservedKey()))
            {
                // Build is completed, can't reserve
                return -100001;
            }

            var guid = Guid.NewGuid();

            var entry = new ReservationEntry(DisplayName, guid);
            var serializedEntry = JsonSerializer.Serialize(entry);

            var record = new TimelineRecord()
            {
                Id = PhaseId,
                Variables =
                {
                    [$"{SyncParticipantVarPrefix}{guid}"] = new VariableValue(DisplayName, false)
                }
            };

            for (; ; await Task.Delay(TimeSpan.FromSeconds(PollSeconds)))
            {
                var updatedRecord = await UpdateTimelineRecordAsync(record);
                record.Variables.Clear();

                var participants = updatedRecord.Variables.Where(k => k.Key.StartsWith(SyncParticipantVarPrefix)).ToList();

                Console.WriteLine($"Job: '{DisplayName}', Participants: {participants.Count}");


                if (participants.Count >= JobCount)
                {
                    return participants.Count;
                }
            }
        }
        catch
        {
            // Return large negative number to indicate failure
            return -10000;
        }
    }

    private record ReservationEntry(string AgentName, Guid Id);
}
