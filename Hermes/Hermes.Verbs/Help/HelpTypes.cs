using System.ComponentModel;
using Hermes.Core;

namespace Hermes.Verbs.Help;

/// <summary>
/// Arguments for the help Verb.
/// </summary>
[Description("Get help information about available Verbs.")]
public sealed class HelpArgs
{
    /// <summary>
    /// Optional specific Verb name to get help for. If null, lists all Verbs.
    /// </summary>
    [Description("The name of a specific Verb to get help for. If not provided, lists all available Verbs.")]
    public string? Verb { get; init; }

    /// <summary>
    /// Whether to include schema information for the Verb(s).
    /// </summary>
    [Description("Whether to include argument and result schema information. Defaults to false.")]
    public bool IncludeSchema { get; init; } = false;
}

/// <summary>
/// Result of the help Verb.
/// </summary>
[Description("Help information about Verbs.")]
public sealed class HelpResult : VerbResult
{
    /// <summary>
    /// The help information for the requested Verb(s).
    /// </summary>
    [Description("List of Verb information entries.")]
    public required IReadOnlyList<VerbInfo> Verbs { get; init; }
}

/// <summary>
/// Information about a single Verb.
/// </summary>
[Description("Information about a single Verb.")]
public sealed class VerbInfo
{
    /// <summary>
    /// The name of the Verb.
    /// </summary>
    [Description("The name of the Verb (e.g., 'fs.readFile').")]
    public required string Name { get; init; }

    /// <summary>
    /// A description of what the Verb does.
    /// </summary>
    [Description("A description of what the Verb does.")]
    public string? Description { get; init; }

    /// <summary>
    /// The schema for the Verb's arguments, if requested.
    /// </summary>
    [Description("The schema definition for the Verb's arguments (only included if IncludeSchema is true).")]
    public string? ArgumentsSchema { get; init; }

    /// <summary>
    /// The schema for the Verb's result, if requested.
    /// </summary>
    [Description("The schema definition for the Verb's result (only included if IncludeSchema is true).")]
    public string? ResultSchema { get; init; }
}
