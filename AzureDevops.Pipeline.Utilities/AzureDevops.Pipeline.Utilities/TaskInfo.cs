namespace AzureDevops.Pipeline.Utilities;

public record TaskInfo(Guid JobId, Guid PlanId, Guid TaskId, Guid TimelineId, string HubName = "build")
{
    public string AllJobsReservedKey() => $"{TaskId}.AllJobsReserved";
}
