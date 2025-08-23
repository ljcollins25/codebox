using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.OAuth;
using Microsoft.VisualStudio.Services.WebApi;

#nullable disable
#nullable enable annotations

namespace AzureDevops.Pipeline.Utilities;

public abstract class TaskOperationBase(IConsole Console)
{
    public required string TaskUrl;
    public required string AdoToken;

    public bool TaskUrlSpecified;

    public double PollSeconds = 1;

    public bool Verbose = false;
    public bool Debug = false;

    protected BuildUri adoBuildUri;
    protected TaskInfo taskInfo;
    protected VssConnection connection;
    protected BuildHttpClient client;
    protected TaskHttpClient taskClient;
    protected TaskAgentHttpClient agentClient;
    public HttpClient HttpClient { get; protected set; }
    protected Build build;
    protected DefinitionReference definition;

    protected Dictionary<Guid, TimelineRecord> RecordsById;

    public async Task<int> RunAsync()
    {
        await InitializeAsync();

        return await RunCoreAsync();
    }

    protected abstract Task<int> RunCoreAsync();

    public static string ToDebugString(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        return "###" + HexConverter.ToString(bytes);
    }

    public static string ChunkSplit(string value, int chunkSize)
    {
        return string.Join("\n", value.Chunk(chunkSize).Select(c => c.AsSpan().ToString()));
    }

    public static string FromDebugString(string token)
    {
        token = token.ReplaceLineEndings("");
        if (token.StartsWith("###"))
        {
            var bytes = HexConverter.ToByteArray(token.Substring(3));
            token = Encoding.UTF8.GetString(bytes);
        }

        if (Helpers.IsAzAccessToken(token))
        {
            //return ":" + token;
        }

        return token;
    }

    protected async ValueTask<IEnumerable<TimelineRecord>> GetAncestorsAndSelfAsync(Guid guid)
    {
        return GetAncestorsAndSelf(await GetRecordAsync(guid));
    }

    private IEnumerable<TimelineRecord> GetAncestorsAndSelf(TimelineRecord record)
    {
        yield return record;

        while (record?.ParentId is { } parentId && RecordsById.TryGetValue(parentId, out var parent))
        {
            yield return parent;
            record = parent;
        }
    }

    public async Task InitializeAsync()
    {
        AdoToken = FromDebugString(AdoToken);

        adoBuildUri = BuildUri.ParseBuildUri(TaskUrl);
        taskInfo = adoBuildUri.DeserializeFromParameters<TaskInfo>();

        VssCredentials credential = Helpers.IsAzAccessToken(AdoToken)
            ? new VssOAuthAccessTokenCredential(AdoToken)
            : new VssBasicCredential(string.Empty, AdoToken);
        connection = new VssConnection(adoBuildUri.OrganizationUri,
            new VssHttpMessageHandler(new VssBasicCredential(AdoToken, string.Empty), VssClientHttpRequestSettings.Default),
            //new VssHttpMessageHandler(credential, VssClientHttpRequestSettings.Default),
            delegatingHandlers: new [] { new InterceptingHandler() });
        HttpClient = new GenericClient(connection).HttpClient;
        client = connection.GetClient<BuildHttpClient>();
        taskClient = connection.GetClient<TaskHttpClient>();
        agentClient = connection.GetClient<TaskAgentHttpClient>();

        if (adoBuildUri.BuildId >= 0)
        {
            build = await client.GetBuildAsync(adoBuildUri.Project, adoBuildUri.BuildId);
            definition = build.Definition;
        }
        else
        {
            definition =  await client.GetDefinitionAsync(adoBuildUri.Project, adoBuildUri.DefinitionId);
        }

        if (Debug)
        {
            Console.WriteLine($"TaskUri:\n{TaskUrl}");
            Console.WriteLine($"Token:\n{ChunkSplit(ToDebugString(AdoToken), 100)}");
            Console.WriteLine($"EndToken");
        }
    }

    public class InterceptingHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await (request.Content?.ReadAsStringAsync() ?? Task.FromResult(string.Empty));
            return await base.SendAsync(request, cancellationToken);
        }
    }

    public class GenericClient : VssHttpClientBase
    {
        public GenericClient(VssConnection conneciton) : base(conneciton.Uri, conneciton.Credentials)
        {
            this.HttpClient = this.Client;
        }

        public HttpClient HttpClient { get; }
    }

    public Task<TaskLog> AppendLogContentAsync(TimelineRecord record, Stream stream)
    {
        return taskClient.AppendLogContentAsync(
            scopeIdentifier: build.Project.Id,
            hubName: taskInfo.HubName,
            planId: taskInfo.PlanId,
            logId: record.Log.Id,
            stream);
    }

    public async Task<TaskLog?> TryGetLogAsync(TimelineRecord record)
    {
        var logs = await GetLogs();

        string path = record.Log == null ? $"logs/{record.Id}" : null;

        return logs.FirstOrDefault(l => path != null
            ? string.Equals(path, l.Path, StringComparison.OrdinalIgnoreCase)
            : l.Id == record.Log.Id);
    }

    public Task<List<TaskLog>> GetLogs()
    {
        return taskClient.GetLogsAsync(
            scopeIdentifier: build.Project.Id,
            hubName: taskInfo.HubName,
            planId: taskInfo.PlanId);
    }

    public Task<TaskLog> CreateLogAsync(TimelineRecord record)
    {
        string path = $"logs/{record.Id}";
        return taskClient.CreateLogAsync(
            scopeIdentifier: build.Project.Id,
            hubName: taskInfo.HubName,
            planId: taskInfo.PlanId,
            log: new TaskLog(path));
    }

    public async Task<IReadOnlyList<string>> GetLogLinesAsync(TimelineRecord record, int? startLine = null, int? endLine = null)
    {
        return await taskClient.GetLogAsync(
            scopeIdentifier: build.Project.Id,
            hubName: taskInfo.HubName,
            planId: taskInfo.PlanId,
            logId: record.Log.Id,
            startLine: startLine,
            endLine: endLine);
    }

    public TaskCompletedEvent GetTaskCompletedEvent(TaskResult result, TimelineRecord? taskRecord = null)
    {
        return new TaskCompletedEvent(
                taskRecord?.ParentId ?? taskInfo.JobId,
                taskRecord?.Id ?? taskInfo.TaskId,
                result
            );
    }

    public Task RaisePlanEventAsync<T>(T eventData)
        where T : JobEvent
    {
        return taskClient.RaisePlanEventAsync(
            scopeIdentifier: build.Project.Id,
            planType: taskInfo.HubName,
            planId: taskInfo.PlanId,
            eventData: eventData);
    }

    public Task<List<TimelineRecord>> UpdateTimelineRecordsAsync(params IEnumerable<TimelineRecord> records)
    {
        return taskClient.UpdateTimelineRecordsAsync(
            scopeIdentifier: build.Project.Id,
            planType: taskInfo.HubName,
            planId: taskInfo.PlanId,
            timelineId: taskInfo.TimelineId,
            records);
    }

    public async ValueTask<TimelineRecord> GetRecordAsync(Guid id, bool forceRefresh = false)
    {
        if (RecordsById == null || forceRefresh)
        {
            await RefreshTimelineRecordsAsync();
        }

        return RecordsById[id];
    }

    public async ValueTask<TimelineRecord?> TryGetRecordAsync(Guid id, bool forceRefresh = false)
    {
        if (RecordsById == null || forceRefresh)
        {
            await RefreshTimelineRecordsAsync();
        }

        return RecordsById.GetValueOrDefault(id);
    }

    public async Task<List<TimelineRecord>> RefreshTimelineRecordsAsync()
    {
        var records = await taskClient.GetRecordsAsync(
            scopeIdentifier: build.Project.Id,
            hubName: taskInfo.HubName,
            planId: taskInfo.PlanId,
            timelineId: taskInfo.TimelineId);

        RecordsById = records.ToDictionary(r => r.Id);

        return records;
    }

    public async Task<TimelineRecord> UpdateTimelineRecordAsync(TimelineRecord record)
    {
        var result = await UpdateTimelineRecordsAsync(record);
        return result[0];
    }

    public async Task SetTaskResult(TaskResult result, TimelineRecord? record = null, IConsole? console = null)
    {
        if (record?.Result == null || record?.State == TimelineRecordState.InProgress)
        {
            console?.WriteLine($"Setting result to {result}");
            await RaisePlanEventAsync(GetTaskCompletedEvent(result));
        }
        else
        {
            console?.WriteLine($"Skipping due to exit attempted result: {result}, actual result: {record.Result}");
        }
    }

    public Task<PropertiesCollection> GetBuildProperties() =>
        client.GetBuildPropertiesAsync(build.Project.Id, build.Id);

    public Task SetBuildProperties(PropertiesCollection properties) =>
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

    [return: NotNullIfNotNull(nameof(id))]
    public static Guid? GetId(string? id, string suffix = "", Guid? parent = default)
    {
        if (id == null) return null;

        id += suffix;
        if (Guid.TryParse(id, out var result))
        {
            return result;
        }

        result = Helpers.GenerateGuidFromString(id);
        if (parent is not null)
        {

            var resultBytes = MemoryMarshal.Cast<Guid, long>(MemoryMarshal.CreateSpan(ref result, 1));
            var parentBytes = MemoryMarshal.Cast<Guid, long>([parent.Value]);
            for (int i = 0; i < resultBytes.Length; i++)
            {
                resultBytes[i] ^= parentBytes[i];
            }
        }

        return result;
    }
}
