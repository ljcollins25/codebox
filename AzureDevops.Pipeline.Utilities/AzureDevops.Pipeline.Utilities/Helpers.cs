using System.Text.RegularExpressions;

namespace AzureDevops.Pipeline.Utilities;

public static class Helpers
{
    public const string TaskUriTemplate = "$(System.CollectionUri)$(System.TeamProject)?buildId=$(Build.BuildId)&jobId=$(System.JobId)&planId=$(System.PlanId)&taskId=$(System.TaskInstanceId)&timelineId=$(System.TimelineId)";

    public static readonly Regex VariableSeparatorPattern = new Regex(@"[\._\-]");
    public static readonly Regex VariablePattern = new Regex(@"\$\(([\w\._\-]+)\)");


    public static class Env
    {
        public static readonly Optional<string> TaskUri = ExpandVariables(TaskUriTemplate, requireAll: true).AsNonEmptyOrOptional();
        public static readonly Optional<int> TotalJobsInPhase = ExpandVariables($"(System.TotalJobsInPhase)", requireAll: true).AsNonEmptyOrOptional().Then<int>(v => int.TryParse(v, null, out var i) ? i : default);
        public static readonly Optional<int> JobPositionInPhase = ExpandVariables($"(System.JobPositionInPhase)", requireAll: true).AsNonEmptyOrOptional().Then<int>(v => int.TryParse(v, null, out var i) ? i : default);
        public static readonly Optional<Guid> JobId = ExpandVariables($"(System.JobId)", requireAll: true).AsNonEmptyOrOptional().Then<Guid>(v => Guid.TryParse(v, null, out var i) ? i : default);
        public static readonly Optional<string> JobDisplayName = ExpandVariables($"(System.JobDisplayName)", requireAll: true).AsNonEmptyOrOptional();
        public static readonly Optional<Guid> PhaseId = ExpandVariables($"(System.PhaseId)", requireAll: true).AsNonEmptyOrOptional().Then<Guid>(v => Guid.TryParse(v, null, out var i) ? i : default);
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
            string envValue = Environment.GetEnvironmentVariable(envName);

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

    public static Optional<string> AsNonEmptyOrOptional(this string s)
    {
        return string.IsNullOrEmpty(s) ? default : s;
    }

    public static string AsEnvironmentVariableName(string variableName)
    {
        return VariableSeparatorPattern.Replace(variableName, m => "_").ToUpperInvariant();
    }
}
