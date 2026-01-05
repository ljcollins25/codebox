namespace Hermes.Core;

/// <summary>
/// Interface for Verb handlers that execute with typed arguments and return typed results.
/// </summary>
/// <typeparam name="TArgs">The type of arguments for this Verb.</typeparam>
/// <typeparam name="TResult">The type of result returned by this Verb.</typeparam>
public interface IVerb<TArgs, TResult>
    where TResult : VerbResult
{
    TResult Execute(TArgs args);
}
