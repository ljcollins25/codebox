using System;
using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;

namespace AzureDevops.Pipeline.Utilities;

public abstract class LogOperationBase(IConsole Console) : TaskOperationBase(Console)
{
    public int? StartLine;

    public int? EndLine;

    public string? StartLinePattern;

    public string? EndLinePattern;

    public string Prefix = string.Empty;

    public List<string> HeaderLines = new List<string>();

    protected bool NeedsPreprocessing => (StartLine != null || EndLine != null || StartLinePattern != null || EndLinePattern != null || HeaderLines.Count != 0 || !string.IsNullOrEmpty(Prefix));

    public async Task<IEnumerable<string>> GetProcessedLogLinesAsync(TimelineRecord record)
    {
        if (StartLine < 0)
        {
            EndLine = StartLine;
        }

        var logLines = await GetLogLinesAsync(record, StartLine, EndLine);

        return getLines();

        IEnumerable<string> getLines()
        {
            foreach (var line in HeaderLines)
            {
                yield return Prefix + line;
            }

            var startRegex = StartLinePattern.AsNonEmptyOrOptional().Select(p => new Regex(p!)).Value;
            var endRegex = EndLinePattern.AsNonEmptyOrOptional().Select(p => new Regex(p!)).Value;

            foreach (var line in logLines)
            {
                if (startRegex != null && !startRegex.IsMatch(line))
                {
                    continue;
                }

                startRegex = null;
                yield return Prefix + line;

                if (endRegex?.IsMatch(line) == true)
                {
                    break;
                }
            }
        }
    }
}
