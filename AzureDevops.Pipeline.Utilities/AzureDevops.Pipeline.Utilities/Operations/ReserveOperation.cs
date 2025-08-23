using System.CommandLine;
using System.Text;
using System.Text.Json;

namespace AzureDevops.Pipeline.Utilities;

public class ReserveOperation(IConsole Console) : TaskOperationBase(Console)
{
    public string? AgentName;
    public required int JobCount;
    public bool CheckOnly = false;

    public string ReservationPrefix = $"****reservations:";

    protected override async Task<int> RunCoreAsync()
    {
        try
        {
            var props = await GetBuildProperties();

            if (IsCompleted(build) || props.ContainsKey(taskInfo.AllJobsReservedKey()))
            {
                // Build is completed, can't reserve
                return -100001;
            }
            else if (CheckOnly)
            {
                AppendLinesToEnvFile(FileEnvVar.GITHUB_OUTPUT,
                    $"{OutputNames.hasMoreJobs}=true");

                return JobCount;
            }

            var record = await UpdateTimelineRecordAsync(new() { Id = taskInfo.TaskId });


            var entry = new ReservationEntry(AgentName ?? Environment.MachineName, Guid.NewGuid());

            await AppendLogContentAsync(record, new MemoryStream(Encoding.UTF8.GetBytes(ReservationPrefix + JsonSerializer.Serialize(entry))));

            // Wait some time for log to propagate
            // Without wait we see insertions from other threads may be inserted between entries
            // which breaks consistency
            await Task.Delay(TimeSpan.FromSeconds(PollSeconds));

            var logLines = await GetLogLinesAsync(record);

            using var writer = new StringWriter();
            writer.WriteLine();
            writer.WriteLine("[");
            foreach (var line in logLines)
            {
                if (line.StartsWith(ReservationPrefix))
                {
                    writer.Write(line.AsSpan().Slice(ReservationPrefix.Length));
                    writer.WriteLine(",");
                }
            }
            writer.WriteLine("]");

            writer.Flush();

            var reservations = JsonSerializer.Deserialize<List<ReservationEntry>>(writer.ToString(), BuildUri.SerializerOptions)!;

            int reservationIndex = reservations.IndexOf(entry);

            var verboseOutput = "";
            if (Verbose)
            {
                logLines.ForEach(l => writer.WriteLine(l));
                verboseOutput = writer.ToString();
            }

            Console.WriteLine($"AgentName: '{AgentName}', Reservations: {reservations.Count}, ReservationIndex: {reservationIndex}{verboseOutput}");

            bool isReserved = reservationIndex < JobCount;
            bool isLast = reservationIndex == (JobCount - 1);
            bool hasMoreJobs = !isLast && isReserved;

            if (isLast)
            {
                await SetBuildProperties(
                    new()
                    {
                        [taskInfo.AllJobsReservedKey()] = "1"
                    });
            }

            AppendLinesToEnvFile(FileEnvVar.GITHUB_OUTPUT,
                $"isReserved={isReserved}",
                $"{OutputNames.hasMoreJobs}={hasMoreJobs}");

            if (isReserved)
            {
                AppendLinesToEnvFile(FileEnvVar.GITHUB_ENV,
                    $"Capability.TaskId={taskInfo.TaskId}",
                    $"AZP_URL={adoBuildUri.OrganizationUri}",
                    $"AZP_TOKEN={AdoToken}",
                    $"AZP_JOB_INDEX={reservationIndex}",
                    $"AZP_AGENT_NAME=agent-{adoBuildUri.BuildId}-m{reservationIndex}-r{Environment.GetEnvironmentVariable("GITHUB_RUN_ID")}");
            }

            return isReserved ? reservationIndex : -reservationIndex;
        }
        catch
        {
            // Return large negative number to indicate failure
            return -10000;
        }
    }

    private record ReservationEntry(string AgentName, Guid Id);
}
