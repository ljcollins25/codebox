using System;
using System.Buffers;
using System.Collections;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.Services.Commerce;

namespace AzureDevops.Pipeline.Utilities;

public static class Helpers
{
    public const string TaskUriTemplate = "$(System.CollectionUri)$(System.TeamProject)?buildId=$(Build.BuildId)&jobId=$(System.JobId)&planId=$(System.PlanId)&timelineId=$(System.TimelineId)&taskId=$(System.TaskInstanceId)";

    public static readonly Regex VariableSeparatorPattern = new Regex(@"[\._\-]");
    public static readonly Regex VariablePattern = new Regex(@"\$\(([\w\._\-]+)\)");


    public static class Env
    {
        public static readonly Optional<string> TaskUri = Globals.TaskUrl
            .Or(ExpandVariables($"$({TaskUrlVariable})", requireAll: true).AsNonEmptyOrOptional())
            .Or(ExpandVariables(TaskUriTemplate, requireAll: true).AsNonEmptyOrOptional());

        public static readonly Optional<string> Token = Globals.Token
            .Or(ExpandVariables($"$({AccessTokenVariable})", requireAll: true).AsNonEmptyOrOptional());


        public static readonly Optional<string> CurrentTaskUrl = ExpandVariables(TaskUriTemplate, requireAll: true).AsNonEmptyOrOptional();
        public static readonly Optional<int> TotalJobsInPhase = ExpandVariables("$(System.TotalJobsInPhase)", requireAll: true).AsNonEmptyOrOptional().Then(v => Optional.Try(int.TryParse(v, null, out var i), i));
        public static readonly Optional<int> JobPositionInPhase = ExpandVariables("$(System.JobPositionInPhase)", requireAll: true).AsNonEmptyOrOptional().Then(v => Optional.Try(int.TryParse(v, null, out var i), i));
        public static readonly Optional<Guid> JobId = ExpandVariables("$(System.JobId)", requireAll: true).AsNonEmptyOrOptional().Then(v => Optional.Try(Guid.TryParse(v, null, out var i), i));
        public static readonly Optional<string> JobDisplayName = ExpandVariables("$(System.JobDisplayName)", requireAll: true).AsNonEmptyOrOptional();
        public static readonly Optional<Guid> PhaseId = Globals.PhaseId.Or(ExpandVariables("$(System.PhaseId)", requireAll: true).AsNonEmptyOrOptional()).Then(v => Optional.Try(Guid.TryParse(v, null, out var i), i));
    }

    public static Guid GenerateGuidFromString(string input)
    {
        // Use SHA-1 to hash the input string
        using (MD5 hasher = MD5.Create())
        {
            byte[] hashBytes = hasher.ComputeHash(Encoding.UTF8.GetBytes(input));

            // Create a new GUID from the first 16 bytes of the hash
            byte[] guidBytes = new byte[16];
            Array.Copy(hashBytes, guidBytes, 16);

            return new Guid(guidBytes);
        }
    }

    public static string ExpandVariables(string input, Out<IEnumerable<string>> missingVariables = default, bool requireAll = false)
    {
        missingVariables.Value = Enumerable.Empty<string>();

        if (string.IsNullOrEmpty(input))
            return input;

        List<string> missingVars = null;

        // Replace matches with environment variable values
        string result = VariablePattern.Replace(input, match =>
        {
            string variableName = match.Groups[1].Value;
            string envName = AsEnvironmentVariableName(variableName);
            string? envValue = GetEnvironmentVariable(envName);

            if (string.IsNullOrEmpty(envValue))
            {
                missingVars ??= new();
                missingVars.Add(envName);
                return variableName;
            }

            return envValue;
        });

        if (missingVars != null)
        {
            missingVariables.Value = missingVars;
            if (requireAll)
            {
                return null;
            }
        }

        return result;
    }

    public static async Task<long> CopyToAsync(this Stream sourceStream, Stream targetStream, IConsole console, CancellationToken token = default, int bufferSize = 1 << 20)
    {
        var totalLength = sourceStream.Length;
        long copied = 0;
        var remaining = totalLength;

        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(totalLength, bufferSize));
        try
        {
            Stopwatch watch = Stopwatch.StartNew();
            int bytesRead;
            while ((bytesRead = await sourceStream.ReadAsync(new Memory<byte>(buffer), token)) != 0)
            {
                await targetStream.WriteAsync(new ReadOnlyMemory<byte>(buffer, 0, bytesRead), token);

                copied += bytesRead;
                var percentage = (copied * 100.0) / totalLength;
                Console.WriteLine($"Copied ({percentage.Truncate(1)}%) {copied} of {totalLength} in {watch.Elapsed}");

                watch.Restart();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return copied;
    }

    public static UriBuilder Scrub(this Uri uri)
    {
        var scrubbedUri = new UriBuilder(uri)
        {
            Query = null
        };

        return scrubbedUri;
    }

    public static double Truncate(this double value, int digits)
    {
        return Math.Round(value, digits, MidpointRounding.ToZero);
    }

    public static IEnumerable<KeyValuePair<string, string?>> GetEnvironmentVariables()
    {
        return Environment.GetEnvironmentVariables().OfType<DictionaryEntry>().Select(e => KeyValuePair.Create((string)e.Key, (string?)e.Value));
    }

    public static string? GetEnvironmentVariable(string name)
    {
        string overrideEnvName = OverridePrefix + name;
        return Environment.GetEnvironmentVariable(overrideEnvName).AsNonEmptyOrOptional().Value
                ?? Environment.GetEnvironmentVariable(name);
    }

    public static string? AsNonEmptyOrNull(this string? s)
    {
        return string.IsNullOrEmpty(s) ? null : s;
    }

    public static Optional<string> AsNonEmptyOrOptional(this string? s)
    {
        return string.IsNullOrEmpty(s) ? Optional.Default : s;
    }

    public static Optional<T> Or<T>(this Optional<T> first, Optional<T> second)
    {
        if (first.HasValue) return first;
        else return second;
    }

    public static Optional<T?> ToNullable<T>(this Optional<T> value)
        where T : struct
    {
        if (!value.HasValue) return Optional.Default;

        return value.Value;
    }

    public static T Result<T>(Func<T> getResult)
    {
        return getResult();
    }

    public static async ValueTask<TResult> ThenAsync<T, TResult>(this ValueTask<T> task, Func<T, TResult> select)
    {
        var input = await task;
        return select(input);
    }

    public static async ValueTask<TResult> ThenAsync<T, TResult>(this Task<T> task, Func<T, TResult> select)
    {
        var input = await task;
        return select(input);
    }

    public static bool IsNonEmpty([NotNullWhen(true)] this string? s)
    {
        return !string.IsNullOrEmpty(s);
    }

    public static void Add<K,V>(this IDictionary<K,V> map, IEnumerable<KeyValuePair<K, V>> entries)
    {
        foreach (var entry in entries)
        {
            map[entry.Key] = entry.Value;
        }
    }

    public static string AsEnvironmentVariableName(string variableName)
    {
        return VariableSeparatorPattern.Replace(variableName, m => "_").ToUpperInvariant();
    }

    public static void Add(this Command parent, IEnumerable<Command> commands)
    {
        foreach (var command in commands)
        {
            parent.Add(command);
        }
    }

    public static string GetSetPipelineVariableText(string name, string value, bool isSecret = false, bool isOutput = false, bool emit = false, bool log = false)
    {
        string additionalArgs = "";
        if (isSecret)
        {
            additionalArgs += "issecret=true;";
        }

        if (isOutput)
        {
            additionalArgs += "isOutput=true;";
        }

        if (log && emit)
        {
            string valueText = isSecret ? "***" : value;
            Console.WriteLine($"Setting '{name}' to '{valueText}'");
        }

        return PrintAndReturn($"##vso[task.setvariable variable={name};{additionalArgs}]{value}", emit);
    }

    public static string AddBuildTag(string tag, bool print = true)
    {
        return PrintAndReturn($"##vso[build.addbuildtag]{tag}", print: print);
    }

    private static string PrintAndReturn(string value, bool print)
    {
        if (print) Console.WriteLine(value);
        return value;
    }

    private static string? GetReplaceableTokenValue(this string arg) =>
        arg.Length > 1 && arg[0] == '@'
            ? arg.Substring(1)
            : null;

    internal static string[] TryReadResponseFile(string filePath)
    {
        return ExpandResponseFile(filePath).ToArray();

        static IEnumerable<string> ExpandResponseFile(string filePath)
        {
            var lines = File.ReadAllLines(filePath);

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                foreach (var p in SplitLine(line))
                {
                    if (p.GetReplaceableTokenValue() is { } path)
                    {
                        foreach (var q in ExpandResponseFile(path))
                        {
                            yield return q;
                        }
                    }
                    else
                    {
                        yield return p;
                    }
                }
            }
        }

        static IEnumerable<string> SplitLine(string line)
        {
            var arg = line.Trim();

            if (arg.Length == 0 || arg[0] == '#')
            {
                yield break;
            }

            foreach (var word in CommandLineStringSplitter.Instance.Split(arg))
            {
                yield return word;
            }
        }
    }

    public static bool IsAzAccessToken(string jwt)
    {
        string[] parts = jwt.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        string payloadBase64 = parts[1];
        string payloadJson = DecodeBase64Url(payloadBase64);

        return payloadJson.Contains("\"appid\"");
    }

    static string DecodeBase64Url(string input)
    {
        string padded = input
            .Replace('-', '+')
            .Replace('_', '/');

        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }

        byte[] bytes = Convert.FromBase64String(padded);
        return Encoding.UTF8.GetString(bytes);
    }
}
