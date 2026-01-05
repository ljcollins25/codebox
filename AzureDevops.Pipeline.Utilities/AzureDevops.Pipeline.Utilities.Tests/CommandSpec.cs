
using System.CodeDom.Compiler;
using System.CommandLine.Parsing;
using System.ComponentModel;
using System.Drawing.Interop;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AzureDevops.Pipeline.Utilities.Tests;

public class ExecutorTests
{
    [Fact]
    public void TestBasic()
    {
        var executor = new HermesCommandExecutor();
        new HelloWorldCommand().RegisterCommand(executor, "hello");

        var result = executor.Run("""
        {
            "command": "hello",
            "arguments": {
                "sender": "TestBasic"
            }
        }

        """);

        var argsSchema = JsonSchemaBuilder.GetSchema(typeof(HelloWorldArguments), executor.Options);


    }

    public class HelloWorldArguments
    {
        public FileAccess Access { get; set; }

        public DateTime?[] NullableDtArray { get; set; }

        [Description("The message sender")]
        public required string Sender { get; init; }

        public int TestInt { get; set; }
        public long? TestNullableLong { get; set; }

    }

    public class HelloWorldResult : CommandResult
    {
        public required string Response { get; init; }
    }
    public class HelloWorldCommand : IActionCommand<HelloWorldArguments, HelloWorldResult>
    {
        public HelloWorldResult Run(HelloWorldArguments arguments)
        {
            return new HelloWorldResult()
            {
                Succeeded = true,
                Response = $"Hello {arguments.Sender}"
            };
        }
    }
}

public class HermesCommandExecutor
{
    private Dictionary<string, CommandInfo> _commandInfos = new(StringComparer.OrdinalIgnoreCase);

    public JsonSerializerOptions Options { get => field ??= GetOptions(); set; }

    public void RegisterCommand<TArgs, TResult>(string name, IActionCommand<TArgs, TResult> command)
        where TResult : CommandResult
    {
        _commandInfos.Add(name, new CommandInfo<TArgs, TResult>(command) { Executor = this });
    }

    public string Run(string input)
    {
        // TODO: Convert YAML to JSON
        var request = JsonSerializer.Deserialize<CommandRequest>(input, Options);
        var result = request!.Run();
        // TODO: Convert to specified response format
        return JsonSerializer.Serialize((object)result, Options);
    }

    public abstract record CommandInfo
    {
        public required HermesCommandExecutor Executor { get; init; }
        public string ArgumentsSchema => field ??= GetArgumentsSchema();
        public string ResultsSchema => field ??= GetResultsSchema();

        protected abstract string GetResultsSchema();

        protected abstract string GetArgumentsSchema();

        public abstract CommandRequest Read(JsonElement element, JsonSerializerOptions options);

        public abstract void Write(Utf8JsonWriter writer, CommandRequest value, JsonSerializerOptions options);
    }


    public record CommandInfo<TArgs, TResult>(IActionCommand<TArgs, TResult> Command) : CommandInfo
        where TResult : CommandResult
    {
        protected override string GetArgumentsSchema() => GetSchema<TArgs>();

        protected override string GetResultsSchema() => GetSchema<TResult>();

        public string GetSchema<T>()
        {
            // TODO: Use JsonTypeInfo and traverse json tree to construct schema
            throw new NotImplementedException();
        }

        public override CommandRequest Read(JsonElement element, JsonSerializerOptions options)
        {
            var request = JsonSerializer.Deserialize<CommandRequest<TArgs, TResult>>(element, options);
            request?.Action = Command;
            return request!;
        }

        public override void Write(Utf8JsonWriter writer, CommandRequest value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, (CommandRequest<TArgs, TResult>)value, options);
        }
    }

    private class Converter(HermesCommandExecutor executor) : JsonConverter<CommandRequest>
    {
        private static readonly string CommandLower = nameof(CommandRequest.Command).ToLower();
        public override CommandRequest? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var element = JsonElement.ParseValue(ref reader);
            if (element.TryGetProperty(CommandLower, out var property) ||
                element.TryGetProperty(nameof(CommandRequest.Command), out property))
            {
                var command = property.GetString()!;
                var commandInfo = executor._commandInfos[command];
                return commandInfo.Read(element, options);
            }

            throw new NotImplementedException();
        }

        public override void Write(Utf8JsonWriter writer, CommandRequest value, JsonSerializerOptions options)
        {
            var commandInfo = executor._commandInfos[value.Command];
            commandInfo.Write(writer, value, options);
        }
    }

    public JsonSerializerOptions GetOptions()
    {
        var options = new JsonSerializerOptions()
        {
            AllowOutOfOrderMetadataProperties = true,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
            IndentSize = 4,
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            Converters =
            {
                new Converter(this),
                new StringToBoolConverter()
            }
        };

        return options;
    }
}

public class JsonSchemaBuilder
{
    public static string GetSchema(Type type, JsonSerializerOptions options)
    {
        var types = new Dictionary<Type, TypeSchema>();
        var typeSpecs = new Dictionary<Type, string>();

        addType(type);

        var stringWriter = new StringWriter();
        var writer = new IndentedTextWriter(stringWriter, " ");
        foreach (var t in types.Values)
        {
            t.Write(writer);
        }

        return stringWriter.ToString();

        string addType(Type? type)
        {
            if (type == null) return "";

            if (typeSpecs.TryGetValue(type, out var spec)) return spec;

            var result = addTypeSchema(type);
            typeSpecs[type] = result;
            return result;
        }

        string addTypeSchema(Type type)
        {
            var typeInfo = options.GetTypeInfo(type);
            if (typeInfo.ElementType != null)
            {
                return addType(typeInfo.ElementType);
            }

            if (typeInfo.Kind != JsonTypeInfoKind.Object)
            {
                if (type.IsEnum)
                {
                    return string.Join(" | ", Enum.GetNames(type).Select(n => $"\"{n}\""));
                }
                else
                {
                    return type.Name;
                }
            }

            var description = type.GetCustomAttribute<DescriptionAttribute>()?.Description;
            var schema = new TypeSchema(type.Name, description);
            types.Add(type, schema);
            var spec = type.Name;
            typeSpecs[type] = spec;

            foreach (var prop in typeInfo.Properties)
            {
                var propertyDescription = prop.AttributeProvider?.GetCustomAttributes(true)?.OfType<DescriptionAttribute>().FirstOrDefault()?.Description;
                var name = prop.Name;
                var isOptional = !prop.IsRequired;
                if (isOptional)
                {
                    name += "?";
                }

                var pType = prop.PropertyType;
                string pTypeSpec;

                var pTypeInfo = options.GetTypeInfo(prop.PropertyType);
                if (pTypeInfo.Kind == JsonTypeInfoKind.Enumerable)
                {
                    addType(pTypeInfo.ElementType);
                    pTypeSpec = $$"""{{addType(pTypeInfo.ElementType)}}[]""";
                }
                else if (pTypeInfo.Kind == JsonTypeInfoKind.Dictionary)
                {
                    pTypeSpec = $$"""{ [key: {{addType(pTypeInfo.KeyType)}}]: {{addType(pTypeInfo.ElementType)}} }""";
                }
                else
                {
                    pTypeSpec = addType(pTypeInfo.ElementType ?? pType);
                }

                schema.Properties.Add(new PropertySchema(name, pTypeSpec, propertyDescription));
            }

            return type.Name;
        }

    }
}

public abstract record MemberSchemaBase(string? Description)
{
    public void Write(IndentedTextWriter writer)
    {
        if (!string.IsNullOrEmpty(Description))
        {
            writer.Write("// ");
            writer.WriteLine(Description);
        }

        WriteCore(writer);
    }

    protected abstract void WriteCore(IndentedTextWriter writer);
}

public record TypeSchema(string Name, string? Description) : MemberSchemaBase(Description)
{
    public List<PropertySchema> Properties { get; } = new();

    protected override void WriteCore(IndentedTextWriter writer)
    {
        writer.WriteLine($"type {Name}");
        writer.WriteLine("{");
        writer.Indent += 2;
        foreach (var property in Properties)
        {
            property.Write(writer);
        }

        writer.Indent -= 2;
        writer.WriteLine("}");
    }
}

public record PropertySchema(string Name, string Type, string? Description) : MemberSchemaBase(Description)
{
    protected override void WriteCore(IndentedTextWriter writer)
    {
        writer.WriteLine($"{Name}: {Type}");
    }
}

public static class ActionCommandExtensions
{
    public static void RegisterCommand<TArgs, TResult>(this IActionCommand<TArgs, TResult> command, HermesCommandExecutor executor, string name)
        where TResult : CommandResult
    {
        executor.RegisterCommand<TArgs, TResult>(name, command);
    }

    public static void Add<T>(this IList<T> list, IEnumerable<T> values)
    {
        foreach (var value in values)
        {
            list.Add(value);
        }
    }
}

public enum ResponseFormat
{
    Json,
    Yaml
}

public abstract class CommandRequest
{
    public required string Command { get; set; }
    public ResponseFormat ResponseFormat { get; set; }
    public abstract CommandResult Run();
}

public class CommandRequest<TArgs, TResult> : CommandRequest
    where TResult : CommandResult
{
    public required TArgs Arguments { get; set; }

    [JsonIgnore]
    public IActionCommand<TArgs, TResult> Action { get; set; } = default!;

    public override CommandResult Run()
    {
        return Action.Run(Arguments);
    }
}

public interface IActionCommand<TArgs, TResult> : IActionCommand
    where TResult : CommandResult
{
    TResult Run(TArgs arguments);
}

public interface IActionCommand
{

}

public class CommandResult
{
    public required bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class StringToBoolConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,

            JsonTokenType.String => ParseString(reader.GetString()),
            JsonTokenType.Number => ParseNumber(reader),

            _ => throw new JsonException($"Cannot convert {reader.TokenType} to bool.")
        };
    }

    private static bool ParseString(string? s)
    {
        if (s is null)
            throw new JsonException("Null string cannot convert to bool.");

        s = s.Trim().ToLowerInvariant();

        return s switch
        {
            "true" or "1" or "yes" or "y" or "on" => true,
            "false" or "0" or "no" or "n" or "off" => false,
            _ => throw new JsonException($"Invalid boolean string: '{s}'.")
        };
    }

    private static bool ParseNumber(Utf8JsonReader reader)
    {
        if (!reader.TryGetInt64(out long value))
            throw new JsonException("Invalid numeric boolean.");

        return value switch
        {
            0 => false,
            1 => true,
            _ => throw new JsonException("Only 0 or 1 are valid numeric booleans.")
        };
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
    {
        writer.WriteBooleanValue(value);
    }
}