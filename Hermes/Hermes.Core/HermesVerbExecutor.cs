using System.Text.Json;

namespace Hermes.Core;

/// <summary>
/// Registry-based VeRB executor that dispatches VeRB envelopes to registered handlers.
/// </summary>
public sealed class HermesVerbExecutor
{
    private readonly Dictionary<string, IVerbRegistration> _verbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _serializerOptions;

    public HermesVerbExecutor(JsonSerializerOptions serializerOptions)
    {
        _serializerOptions = serializerOptions;
    }

    /// <summary>
    /// Registers a VeRB handler for the given verb name.
    /// </summary>
    public void Register<TArgs, TResult>(string verb, IVerb<TArgs, TResult> handler)
        where TResult : VerbResult
    {
        if (_verbs.ContainsKey(verb))
            throw new InvalidOperationException($"VeRB '{verb}' is already registered.");

        _verbs[verb] = new VerbRegistration<TArgs, TResult>(verb, handler);
    }

    /// <summary>
    /// Gets all registered VeRB names.
    /// </summary>
    public IEnumerable<string> GetRegisteredVerbs() => _verbs.Keys;

    /// <summary>
    /// Executes a VeRB from JSON input and returns the serialized result.
    /// </summary>
    /// <param name="input">JSON input containing the VeRB envelope.</param>
    /// <returns>Serialized JSON result.</returns>
    public string Execute(string input)
    {
        var envelope = JsonSerializer.Deserialize<VerbEnvelope>(input, _serializerOptions)
            ?? throw new InvalidOperationException("Invalid VeRB envelope.");

        if (!_verbs.TryGetValue(envelope.Verb, out var registration))
            throw new InvalidOperationException($"Unknown VeRB '{envelope.Verb}'.");

        var result = registration.Execute(envelope.Arguments, _serializerOptions);
        // Serialize using the actual runtime type to include derived class properties
        return JsonSerializer.Serialize(result, result.GetType(), _serializerOptions);
    }

    /// <summary>
    /// Executes a VeRB from a pre-parsed envelope and returns the result object.
    /// </summary>
    public VerbResult Execute(VerbEnvelope envelope)
    {
        if (!_verbs.TryGetValue(envelope.Verb, out var registration))
            throw new InvalidOperationException($"Unknown VeRB '{envelope.Verb}'.");

        return registration.Execute(envelope.Arguments, _serializerOptions);
    }

    /// <summary>
    /// Gets the argument type for a registered VeRB.
    /// </summary>
    public Type? GetArgumentType(string verb)
    {
        return _verbs.TryGetValue(verb, out var registration) ? registration.ArgumentType : null;
    }

    /// <summary>
    /// Gets the result type for a registered VeRB.
    /// </summary>
    public Type? GetResultType(string verb)
    {
        return _verbs.TryGetValue(verb, out var registration) ? registration.ResultType : null;
    }

    private interface IVerbRegistration
    {
        Type ArgumentType { get; }
        Type ResultType { get; }
        VerbResult Execute(JsonElement args, JsonSerializerOptions options);
    }

    private sealed class VerbRegistration<TArgs, TResult> : IVerbRegistration
        where TResult : VerbResult
    {
        private readonly string _verb;
        private readonly IVerb<TArgs, TResult> _handler;

        public VerbRegistration(string verb, IVerb<TArgs, TResult> handler)
        {
            _verb = verb;
            _handler = handler;
        }

        public Type ArgumentType => typeof(TArgs);
        public Type ResultType => typeof(TResult);

        public VerbResult Execute(JsonElement args, JsonSerializerOptions options)
        {
            var typedArgs = args.Deserialize<TArgs>(options)
                ?? throw new InvalidOperationException($"Failed to deserialize arguments for VeRB '{_verb}'.");

            return _handler.Execute(typedArgs);
        }
    }
}
