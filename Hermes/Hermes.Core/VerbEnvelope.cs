using System.Text.Json;

namespace Hermes.Core;

/// <summary>
/// Envelope containing a Verb name and its arguments.
/// </summary>
public sealed class VerbEnvelope
{
    public required string Verb { get; init; }
    public required JsonElement Arguments { get; init; }
}
