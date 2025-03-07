using System;
using System.Collections.Immutable;
using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;

namespace AzureDevops.Pipeline.Utilities;

public class LogExtractOperation(IConsole Console) : TaskOperationBase(Console)
{
    public enum MissingBehaviors
    {
        Skip,
        Empty,
        EnvironmentFallback,
    }

    public Guid? SourceId;

    public int? StartLine;

    public int? EndLine;

    [StringSyntax("Regex")]
    public required List<string> Patterns;

    public bool IsSecret;

    public bool IsOutput;

    public MissingBehaviors MissingBehavior;

    protected override async Task<int> RunCoreAsync()
    {
        var map = await GetValuesAsync(pollLog: true);
        Console.WriteLine($"Found {map.Count} matches");
        foreach (var entry in map)
        {
            var value = entry.Value;
            if ((MissingBehavior == MissingBehaviors.Skip) && value == null) continue;

            value ??= (MissingBehavior == MissingBehaviors.EnvironmentFallback ? Helpers.GetEnvironmentVariable(entry.Key) : string.Empty);
            Helpers.GetSetPipelineVariableText(entry.Key, value ?? string.Empty, isSecret: IsSecret, isOutput: IsOutput, emit: true, log: true);
        }

        return 0;
    }

    public async Task<Dictionary<string, string?>> GetValuesAsync(bool pollLog = false)
    {
        var regex = new Regex($"(?:{string.Join(")|(?:", Patterns)})");

        int iterations = 0;
        TimelineRecord record;
        do
        {
            record = await GetRecordAsync(SourceId ?? taskInfo.TaskId, forceRefresh: iterations != 0);
            if (record.Log != null) break;

            var log = record.Log = await TryGetLogAsync(record);

            Console.WriteLine($"Log not found for {record.Id}: Trying alternative way to find associated log (success={log != null}).");


            Console.WriteLine($"Log not found for {record.Id}: '{record.Name}'. Waiting 5 seconds...");
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        while (iterations++ < 5);

        var logLines = record.Log == null
            ? Array.Empty<string>()
            : await GetLogLinesAsync(record);

        var logString = string.Join("\n", logLines);

        var map = ExtractVariables(regex, logString);
        return map;
    }

    public static Dictionary<string, string?> ExtractVariables(Regex regex, string logString)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in regex.GetGroupNames().Where(n => !int.TryParse(n, out _)))
        {
            map[name] = null;
        }

        foreach (Match match in regex.Matches(logString))
        {
            foreach (Group group in match.Groups)
            {
                if (group.Success && map.ContainsKey(group.Name))
                {
                    map[group.Name] = group.Value;
                }
            }
        }

        return map;
    }


}
