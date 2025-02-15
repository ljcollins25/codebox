using System;
using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;

namespace AzureDevops.Pipeline.Utilities;

public class LogExtractOperation(IConsole Console) : TaskOperationBase(Console)
{
    public Guid? SourceId;

    public int? StartLine;

    public int? EndLine;

    [StringSyntax("Regex")]
    public required List<string> Patterns;

    public bool IsSecret;

    public bool IsOutput;

    protected override async Task<int> RunCoreAsync()
    {
        Dictionary<string, string> map = await GetValuesAsync();
        foreach (var entry in map)
        {
            Helpers.GetSetPipelineVariableText(entry.Key, entry.Value, isSecret: IsSecret, isOutput: IsOutput, emit: true);
        }

        return 0;
    }

    public async Task<Dictionary<string, string>> GetValuesAsync()
    {
        var regex = new Regex($"({string.Join(")|(", Patterns)})");

        var record = await GetRecordAsync(SourceId ?? taskInfo.TaskId);

        var logLines = await GetLogLinesAsync(record);

        var logString = string.Join("\n", logLines);

        var map = ExtractVariables(regex, logString);
        return map;
    }

    public static Dictionary<string, string> ExtractVariables(Regex regex, string logString)
    {
        var found = new Dictionary<string, string>();
        foreach (Match match in regex.Matches(logString))
        {
            foreach (Group group in match.Groups)
            {
                if (!int.TryParse(group.Name, out _))
                {
                    found[group.Name] = group.Value;
                }
            }
        }

        return found;
    }


}
