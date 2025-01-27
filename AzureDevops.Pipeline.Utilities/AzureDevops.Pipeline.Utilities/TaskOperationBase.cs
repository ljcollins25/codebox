using System;
using System.CommandLine;
using System.Text;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using TimelineRecord = Microsoft.TeamFoundation.DistributedTask.WebApi.TimelineRecord;

#nullable disable
#nullable enable annotations

namespace AzureDevops.Pipeline.Utilities;

public abstract class TaskOperationBase(IConsole Console)
{
    public required string TaskUrl;
    public required string AdoToken;

    public double PollSeconds = 1;

    public bool Verbose = false;
    public bool Debug = false;

    protected BuildUri adoBuildUri;
    protected TaskInfo taskInfo;
    protected VssConnection connection;
    protected BuildHttpClient client;
    protected TaskHttpClient taskClient;
    protected TaskAgentHttpClient agentClient;
    protected Build build;

    public async Task<int> RunAsync()
    {
        await InitilializeAsync();

        return await RunCoreAsync();
    }

    protected abstract Task<int> RunCoreAsync();

    public static string ToDebugString(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        return "###" + HexConverter.ToString(bytes);
    }

    public static string FromDebugString(string token)
    {
        if (token.StartsWith("###"))
        {
            var bytes = HexConverter.ToByteArray(token.Substring(3));
            token = Encoding.UTF8.GetString(bytes);
        }

        return token;
    }

    private async Task InitilializeAsync()
    {
        AdoToken = FromDebugString(AdoToken);

        adoBuildUri = BuildUri.ParseBuildUri(TaskUrl);
        taskInfo = adoBuildUri.DeserializeFromParameters<TaskInfo>();

        connection = new VssConnection(adoBuildUri.OrganizationUri, new VssBasicCredential(AdoToken, string.Empty));
        client = connection.GetClient<BuildHttpClient>();
        taskClient = connection.GetClient<TaskHttpClient>();
        agentClient = connection.GetClient<TaskAgentHttpClient>();

        build = await client.GetBuildAsync(adoBuildUri.Project, adoBuildUri.BuildId);

        if (Debug)
        {
            Console.WriteLine($"TaskUri:\n{TaskUrl}");
            Console.WriteLine($"Token:\n{ToDebugString(AdoToken)}");
        }
    }

    protected Task<List<TimelineRecord>> UpdateTimelineRecordsAsync(IEnumerable<TimelineRecord> records)
    {
        return taskClient.UpdateTimelineRecordsAsync(
            scopeIdentifier: build.Project.Id,
            planType: taskInfo.HubName,
            planId: taskInfo.PlanId,
            timelineId: taskInfo.TimelineId,
            records);
    }

    protected async Task<TimelineRecord> UpdateTimelineRecordAsync(TimelineRecord record)
    {
        var result = await UpdateTimelineRecordsAsync([record]);
        return result[0];
    }

    protected Task<PropertiesCollection> GetBuildProperties() =>
        client.GetBuildPropertiesAsync(build.Project.Id, build.Id);

    protected Task SetBuildProperties(PropertiesCollection properties) =>
        client.UpdateBuildPropertiesAsync(properties, build.Project.Id, build.Id);

    protected enum FileEnvVar
    {
        GITHUB_OUTPUT,
        GITHUB_ENV
    }

    protected enum OutputNames
    {
        hasMoreJobs
    }

    protected void AppendLinesToEnvFile(FileEnvVar file, params string[] lines)
    {
        Console.WriteLine($"Writing: {string.Join(Environment.NewLine, [file.ToString(), .. lines])}");
        if (Environment.GetEnvironmentVariable(file.ToString()) is string fileName && !string.IsNullOrEmpty(fileName))
        {
            File.AppendAllLines(fileName, lines);
        }
    }

    protected bool IsCompleted(Build build)
    {
        var result = build.Result;
        return result != null && result != BuildResult.None;
    }
}
