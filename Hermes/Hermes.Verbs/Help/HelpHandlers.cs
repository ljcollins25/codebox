using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Hermes.Core;

namespace Hermes.Verbs.Help;

/// <summary>
/// Handler implementations for help Verbs.
/// </summary>
public static class HelpHandlers
{
    private static readonly JsonSerializerOptions s_schemaOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    /// <summary>
    /// Gets help information about available Verbs.
    /// </summary>
    /// <param name="args">The help arguments.</param>
    /// <param name="executor">The executor to query for registered verbs.</param>
    /// <returns>Help result containing verb information.</returns>
    public static HelpResult GetHelp(HelpArgs args, HermesVerbExecutor executor)
    {
        IEnumerable<IVerbRegistration> registrations;

        if (args.Verb is not null)
        {
            var registration = executor.GetRegistration(args.Verb);
            if (registration is null)
            {
                return new HelpResult
                {
                    Succeeded = false,
                    ErrorMessage = $"Verb '{args.Verb}' not found.",
                    Verbs = []
                };
            }
            registrations = [registration];
        }
        else
        {
            registrations = executor.GetRegistrations()
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
        }

        var verbs = registrations.Select(r => new VerbInfo
        {
            Name = r.Name,
            Description = r.Description ?? "No description available.",
            ArgumentsSchema = args.IncludeSchema
                ? JsonSchemaBuilder.GetSchema(r.ArgumentType, s_schemaOptions)
                : null,
            ResultSchema = args.IncludeSchema
                ? JsonSchemaBuilder.GetSchema(r.ResultType, s_schemaOptions)
                : null
        }).ToList();

        return new HelpResult
        {
            Succeeded = true,
            Verbs = verbs
        };
    }
}
