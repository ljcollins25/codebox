using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;

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
    public HttpClient HttpClient { get; protected set; }
    protected Build build;

    protected Dictionary<Guid, TimelineRecord> RecordsById;

    public async Task<int> RunAsync()
    {
        try
        {
            await InitilializeAsync();

            return await RunCoreAsync();
        }
        catch
        {
            return -1000000;
        }
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

    public async Task InitilializeAsync()
    {
        AdoToken = FromDebugString(AdoToken);

        adoBuildUri = BuildUri.ParseBuildUri(TaskUrl);
        taskInfo = adoBuildUri.DeserializeFromParameters<TaskInfo>();

        connection = new VssConnection(adoBuildUri.OrganizationUri, new VssBasicCredential(AdoToken, string.Empty));
        HttpClient = new GenericClient(connection).HttpClient;
        client = connection.GetClient<BuildHttpClient>();
        taskClient = connection.GetClient<TaskHttpClient>();
        agentClient = connection.GetClient<TaskAgentHttpClient>();

        build = await client.GetBuildAsync(adoBuildUri.Project, adoBuildUri.BuildId);

        if (Debug)
        {
            Console.WriteLine($"TaskUri:\n{TaskUrl}");
            Console.WriteLine($"Token:\n{ChunkSplit(ToDebugString(AdoToken), 100)}");
            Console.WriteLine($"EndToken");
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

    public Task<List<string>> GetLogLinesAsync(TimelineRecord record)
    {
        return taskClient.GetLogAsync(
            scopeIdentifier: build.Project.Id,
            hubName: taskInfo.HubName,
            planId: taskInfo.PlanId,
            logId: record.Log.Id);
    }

    public Task<List<TimelineRecord>> UpdateTimelineRecordsAsync(IEnumerable<TimelineRecord> records)
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
        var result = await UpdateTimelineRecordsAsync([record]);
        return result[0];
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
