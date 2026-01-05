namespace Hermes.Core;

/// <summary>
/// Base class for all VeRB results.
/// </summary>
public abstract class VerbResult
{
    public bool Succeeded { get; init; } = true;
    public string? ErrorMessage { get; init; }
}
