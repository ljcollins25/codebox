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
        var map = await GetValuesAsync();
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

    public async Task<Dictionary<string, string?>> GetValuesAsync()
    {
        var regex = new Regex($"(?:{string.Join(")|(?:", Patterns)})");

        var record = await GetRecordAsync(SourceId ?? taskInfo.TaskId);

        var logLines = await GetLogLinesAsync(record);

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
