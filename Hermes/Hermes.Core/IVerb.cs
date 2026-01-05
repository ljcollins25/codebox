namespace Hermes.Core;

/// <summary>
/// Interface for VeRB handlers that execute with typed arguments and return typed results.
/// </summary>
/// <typeparam name="TArgs">The type of arguments for this VeRB.</typeparam>
/// <typeparam name="TResult">The type of result returned by this VeRB.</typeparam>
public interface IVerb<TArgs, TResult>
    where TResult : VerbResult
{
    TResult Execute(TArgs args);
}
