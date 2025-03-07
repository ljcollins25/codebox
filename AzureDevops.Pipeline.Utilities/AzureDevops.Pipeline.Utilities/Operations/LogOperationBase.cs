using System;
using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Runtime.InteropServices;
using System.Text.Json;
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

    public string? Format;

    public string ReplacementToken = "$(Line)";

    public List<string> HeaderLines = new List<string>();

    public bool FormatHeaders;

    public EscapingMode Escaping;

    protected bool NeedsPreprocessing => (Format != null || Escaping != EscapingMode.None || StartLine != null || EndLine != null || StartLinePattern != null || EndLinePattern != null || HeaderLines.Count != 0 || !string.IsNullOrEmpty(Prefix));

    public async Task<IEnumerable<WritableLine>> GetProcessedLogLinesAsync(TimelineRecord record)
    {
        if (StartLine < 0)
        {
            EndLine = StartLine;
        }

        var logLines = await GetLogLinesAsync(record, StartLine, EndLine);
        WritableLine wl = default;
        wl.Escaping = Escaping;

        if (Prefix != null || Format != null || Escaping != EscapingMode.None)
        {
            var lineSegment = new List<string?>();
            //if (Escaping == EscapingMode.Csv) lineSegment.Add("\"");
            lineSegment.Add(null);
            //if (Escaping == EscapingMode.Csv) lineSegment.Add("\"");

            var parts = new List<string?>();
            if (Prefix != null) parts.Add(Prefix);

            if (Format != null && !string.IsNullOrEmpty(ReplacementToken))
            {
                for (int i = 0; i < Format.Length; i++)
                {
                    var index = Format.IndexOf(ReplacementToken);
                    if (index < 0) index = Format.Length;

                    if (i < index)
                    {
                        parts.Add(Format[i..index]);
                    }

                    if (index < Format.Length)
                    {
                        parts.AddRange(lineSegment);
                    }


                    i = index + ReplacementToken.Length;
                }
            }
            else
            {
                parts.AddRange(lineSegment);
            }

            wl.Parts = parts.ToArray();
        }

        var preformatLines = getLines();
        if (FormatHeaders)
        {
            preformatLines = HeaderLines.Concat(preformatLines);
        }

        var formattedLines = preformatLines.Select(line => wl with { Line = line });
        if (!FormatHeaders)
        {
            formattedLines = HeaderLines.Select(l => new WritableLine(l)).Concat(formattedLines);
        }

        return formattedLines;

        IEnumerable<string> getLines()
        {
            var startRegex = StartLinePattern.AsNonEmptyOrOptional().Select(p => new Regex(p!)).Value;
            var endRegex = EndLinePattern.AsNonEmptyOrOptional().Select(p => new Regex(p!)).Value;

            foreach (var line in logLines)
            {
                if (startRegex != null && !startRegex.IsMatch(line))
                {
                    continue;
                }

                startRegex = null;
                yield return line;

                if (endRegex?.IsMatch(line) == true)
                {
                    break;
                }
            }
        }
    }

    public enum EscapingMode
    {
        None,
        Json,
        Csv
    }

    public static string Escape(string line, EscapingMode escaping)
    {
        switch (escaping)
        {
            case EscapingMode.None:
                return line;
            case EscapingMode.Json:
                return JsonEncodedText.Encode(line).ToString();
            case EscapingMode.Csv:
                return EscapeCsvValue(line);
            default:
                throw Contract.AssertFailure($"Unexpected escaping mode: {escaping}");
        }
    }

    private static string EscapeCsvValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        bool containsSpecialCharacters = value.AsSpan().ContainsAny("\"\r\n");

        if (containsSpecialCharacters)
        {
            // Escape double quotes by doubling them
            value = value.Replace("\"", "\"\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        return value;
    }

    public record struct WritableLine(string Line, EscapingMode Escaping = default, string?[]? Parts = null)
    {
        public override string ToString()
        {
            var line = Escape(Line, Escaping);
            return Parts == null
                ? line
                : string.Concat(Parts.Select(p => p ?? line));
        }

        public void WriteLine(TextWriter writer)
        {
            var line = Escape(Line, Escaping);
            if (Parts == null)
            {
                writer.WriteLine(line);
                return;
            }

            foreach (var part in Parts)
            {
                writer.Write(part ?? line);
            }

            writer.WriteLine();
        }

        public static implicit operator string(WritableLine line) => line.ToString();

        public static implicit operator WritableLine(string value) => new(value);
    }
}
