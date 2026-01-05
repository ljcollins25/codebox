using Hermes.Core;

namespace Hermes.Verbs.Process;

// proc.run
public sealed class ProcRunArgs
{
    public required string Executable { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
}

public sealed class ProcRunResult : VerbResult
{
    public required int ExitCode { get; init; }
    public required string StdoutPath { get; init; }
    public required string StderrPath { get; init; }
}
