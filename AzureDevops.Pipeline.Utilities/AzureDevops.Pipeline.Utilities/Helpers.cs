using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AzureDevops.Pipeline.Utilities;

public static class Helpers
{
    public const string TaskUriTemplate = "$(System.CollectionUri)$(System.TeamProject)?buildId=$(Build.BuildId)&jobId=$(System.JobId)&planId=$(System.PlanId)&taskId=$(System.TaskInstanceId)&timelineId=$(System.TimelineId)";

    public static readonly Regex VariableSeparatorPattern = new Regex(@"[\._\-]");
    public static readonly Regex VariablePattern = new Regex(@"\$\(([\w\._\-]+)\)");


    public static class Env
    {
        public static readonly Optional<string> TaskUri = Globals.TaskUrl
            .Or(ExpandVariables($"$({TaskUrlVariable})", requireAll: true).AsNonEmptyOrOptional())
            .Or(ExpandVariables(TaskUriTemplate, requireAll: true).AsNonEmptyOrOptional());
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
            string overrideEnvName = OverridePrefix + envName;
            string? envValue = Environment.GetEnvironmentVariable(overrideEnvName).AsNonEmptyOrOptional().Value
                ?? Environment.GetEnvironmentVariable(envName);

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

    public static Optional<string> AsNonEmptyOrOptional(this string? s)
    {
        return string.IsNullOrEmpty(s) ? default : s;
    }

    public static Optional<T> Or<T>(this Optional<T> first, Optional<T> second)
    {
        if (first.HasValue) return first;
        else return second;
    }

    public static Optional<T?> ToNullable<T>(this Optional<T> value)
        where T : struct
    {
        if (!value.HasValue) return default;

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
}
