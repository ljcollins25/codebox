namespace Hermes.Core;

/// <summary>
/// A delegate-based implementation of IVerb that allows inline definition of VeRB handlers.
/// </summary>
/// <typeparam name="TArgs">The type of arguments for this VeRB.</typeparam>
/// <typeparam name="TResult">The type of result returned by this VeRB.</typeparam>
public sealed class DelegateVerb<TArgs, TResult> : IVerb<TArgs, TResult>
    where TResult : VerbResult
{
    private readonly Func<TArgs, TResult> _handler;

    /// <summary>
    /// Creates a new delegate-based VeRB handler.
    /// </summary>
    /// <param name="handler">The function to execute when this VeRB is invoked.</param>
    public DelegateVerb(Func<TArgs, TResult> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <inheritdoc />
    public TResult Execute(TArgs args) => _handler(args);

    /// <summary>
    /// Implicitly converts a delegate to a DelegateVerb.
    /// </summary>
    public static implicit operator DelegateVerb<TArgs, TResult>(Func<TArgs, TResult> handler) => new(handler);
}

/// <summary>
/// Extension methods for registering delegate-based VeRBs.
/// </summary>
public static class DelegateVerbExtensions
{
    /// <summary>
    /// Registers a delegate-based VeRB handler.
    /// </summary>
    public static void Register<TArgs, TResult>(
        this HermesVerbExecutor executor,
        string verb,
        Func<TArgs, TResult> handler)
        where TResult : VerbResult
    {
        executor.Register(verb, new DelegateVerb<TArgs, TResult>(handler));
    }
}
