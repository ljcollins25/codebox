using System.ComponentModel;
using Hermes.Core;

namespace Hermes.Verbs.Process;

/// <summary>
/// Arguments for the proc.run Verb.
/// </summary>
[Description("Execute a process and capture its output.")]
public sealed class ProcRunArgs
{
    /// <summary>
    /// The executable to run.
    /// </summary>
    [Description("The path to the executable or command to run.")]
    public required string Executable { get; init; }

    /// <summary>
    /// The command-line arguments to pass to the executable.
    /// </summary>
    [Description("The list of command-line arguments to pass to the executable.")]
    public required IReadOnlyList<string> Arguments { get; init; }
}

/// <summary>
/// Result of the proc.run Verb.
/// </summary>
[Description("The result of executing a process.")]
public sealed class ProcRunResult : VerbResult
{
    /// <summary>
    /// The exit code returned by the process.
    /// </summary>
    [Description("The exit code returned by the process. Zero typically indicates success.")]
    public required int ExitCode { get; init; }

    /// <summary>
    /// The path to the file containing the process's standard output.
    /// </summary>
    [Description("The path to the file containing the captured standard output of the process.")]
    public required string StdoutPath { get; init; }

    /// <summary>
    /// The path to the file containing the process's standard error.
    /// </summary>
    [Description("The path to the file containing the captured standard error of the process.")]
    public required string StderrPath { get; init; }
}
