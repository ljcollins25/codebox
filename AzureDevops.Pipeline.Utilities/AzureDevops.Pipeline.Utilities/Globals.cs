namespace AzureDevops.Pipeline.Utilities;

public static class Globals
{
    public static Optional<string> TaskUrl { get; set; }
    public static Optional<string> Token { get; set; }
    public static Optional<string> PhaseId { get; set; }

    public static string? GeneratedSas;
}

public enum RecordTypes
{
    Task,
    Job,
    Phase,
    Stage
}
