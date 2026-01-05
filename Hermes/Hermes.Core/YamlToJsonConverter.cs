using System.Text.Json;
using YamlDotNet.Serialization;

namespace Hermes.Core;

/// <summary>
/// Converts YAML input to JSON for processing.
/// </summary>
public static class YamlToJsonConverter
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder().Build();
    private static readonly ISerializer JsonSerializer = new SerializerBuilder()
        .JsonCompatible()
        .Build();

    /// <summary>
    /// Converts YAML string to JSON string.
    /// </summary>
    /// <param name="yaml">YAML input string.</param>
    /// <returns>JSON string representation.</returns>
    public static string Convert(string yaml)
    {
        var yamlObject = YamlDeserializer.Deserialize(new StringReader(yaml));
        return JsonSerializer.Serialize(yamlObject);
    }

    /// <summary>
    /// Determines if the input appears to be YAML (not JSON).
    /// </summary>
    public static bool IsLikelyYaml(string input)
    {
        var trimmed = input.TrimStart();
        // JSON starts with { or [, YAML typically doesn't for object notation
        return !trimmed.StartsWith('{') && !trimmed.StartsWith('[');
    }

    /// <summary>
    /// Normalizes input to JSON, converting from YAML if necessary.
    /// </summary>
    public static string NormalizeToJson(string input)
    {
        if (IsLikelyYaml(input))
        {
            return Convert(input);
        }
        return input;
    }
}
