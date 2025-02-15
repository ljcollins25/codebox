using System.CommandLine;
using Microsoft.TeamFoundation.DistributedTask.WebApi;

namespace AzureDevops.Pipeline.Utilities;

public class SynchronizeOperation(IConsole Console) : TaskOperationBase(Console)
{
    public required Guid PhaseId;
    public required int JobCount;
    public required string CorrelationKey;

    public string DisplayName = Environment.MachineName;

    public string SyncParticipantTimestampVar = $"SyncParticipantTimestamp";
    public string SyncParticipantVarPrefix = $"SyncParticipant.";


    protected override async Task<int> RunCoreAsync()
    {
        string allJobsRegisteredKey = $"{PhaseId}";

        var guid = Guid.NewGuid();

        var record = new TimelineRecord()
        {
            Id = PhaseId,
            Variables =
                {
                    [$"{SyncParticipantVarPrefix}{CorrelationKey}{guid}"] = new VariableValue(DisplayName, false)
                }
        };

        for (; ; await Task.Delay(TimeSpan.FromSeconds(PollSeconds)))
        {
            var updatedRecord = await UpdateTimelineRecordAsync(record);
            record.Variables.Clear();

            var participants = updatedRecord.Variables.Where(k => k.Key.StartsWith(SyncParticipantVarPrefix)).ToList();

            Console.WriteLine($"Job: '{DisplayName}', Participants: {participants.Count}, RequiredParticipants: {JobCount}");

            if (participants.Count >= JobCount)
            {
                return 0;
            }
        }
    }

    private record ParticipantEntry(string AgentName, DateTime TimeStamp);
}
