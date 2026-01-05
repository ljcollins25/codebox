using System.ComponentModel;
using System.Text.Json;

namespace Hermes.Core;

/// <summary>
/// Represents a registered Verb with its metadata.
/// </summary>
public interface IVerbRegistration
{
    /// <summary>
    /// The name of the Verb.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The argument type for this Verb.
    /// </summary>
    Type ArgumentType { get; }

    /// <summary>
    /// The result type for this Verb.
    /// </summary>
    Type ResultType { get; }

    /// <summary>
    /// The description from the Description attribute on the args type, if present.
    /// </summary>
    string? Description { get; }

    /// <summary>
    /// Executes the Verb with the given JSON arguments.
    /// </summary>
    VerbResult Execute(JsonElement args, JsonSerializerOptions options);
}

/// <summary>
/// Registry-based Verb executor that dispatches Verb envelopes to registered handlers.
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
    /// Registers a Verb handler for the given verb name.
    /// </summary>
    public void Register<TArgs, TResult>(string verb, IVerb<TArgs, TResult> handler)
        where TResult : VerbResult
    {
        if (_verbs.ContainsKey(verb))
            throw new InvalidOperationException($"Verb '{verb}' is already registered.");

        _verbs[verb] = new VerbRegistration<TArgs, TResult>(verb, handler);
    }

    /// <summary>
    /// Gets all registered Verb registrations.
    /// </summary>
    public IEnumerable<IVerbRegistration> GetRegistrations() => _verbs.Values;

    /// <summary>
    /// Gets a specific Verb registration by name, or null if not found.
    /// </summary>
    public IVerbRegistration? GetRegistration(string verb)
    {
        return _verbs.TryGetValue(verb, out var registration) ? registration : null;
    }

    /// <summary>
    /// Executes a Verb from JSON input and returns the serialized result.
    /// </summary>
    /// <param name="input">JSON input containing the Verb envelope.</param>
    /// <returns>Serialized JSON result.</returns>
    public string Execute(string input)
    {
        var envelope = JsonSerializer.Deserialize<VerbEnvelope>(input, _serializerOptions)
            ?? throw new InvalidOperationException("Invalid Verb envelope.");

        if (!_verbs.TryGetValue(envelope.Verb, out var registration))
            throw new InvalidOperationException($"Unknown Verb '{envelope.Verb}'.");

        var result = registration.Execute(envelope.Arguments, _serializerOptions);
        // Serialize using the actual runtime type to include derived class properties
        return JsonSerializer.Serialize(result, result.GetType(), _serializerOptions);
    }

    /// <summary>
    /// Executes a Verb from a pre-parsed envelope and returns the result object.
    /// </summary>
    public VerbResult Execute(VerbEnvelope envelope)
    {
        if (!_verbs.TryGetValue(envelope.Verb, out var registration))
            throw new InvalidOperationException($"Unknown Verb '{envelope.Verb}'.");

        return registration.Execute(envelope.Arguments, _serializerOptions);
    }

    private sealed class VerbRegistration<TArgs, TResult> : IVerbRegistration
        where TResult : VerbResult
    {
        private readonly IVerb<TArgs, TResult> _handler;

        public VerbRegistration(string verb, IVerb<TArgs, TResult> handler)
        {
            Name = verb;
            _handler = handler;
        }

        public string Name { get; }
        public Type ArgumentType => typeof(TArgs);
        public Type ResultType => typeof(TResult);

        public string? Description => ArgumentType
            .GetCustomAttributes(typeof(DescriptionAttribute), false)
            .OfType<DescriptionAttribute>()
            .FirstOrDefault()?.Description;

        public VerbResult Execute(JsonElement args, JsonSerializerOptions options)
        {
            var typedArgs = args.Deserialize<TArgs>(options)
                ?? throw new InvalidOperationException($"Failed to deserialize arguments for Verb '{Name}'.");

            return _handler.Execute(typedArgs);
        }
    }
}
