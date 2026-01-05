using System.CodeDom.Compiler;
using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Hermes.Core;

/// <summary>
/// Generates a lightweight, descriptive IDL schema from C# types using System.Text.Json metadata.
/// This is NOT JSON Schema - it prioritizes readability and practical introspection.
/// </summary>
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

        Type? GetElementType(Type type)
        {
            // Handle arrays
            if (type.IsArray)
                return type.GetElementType();

            // Handle IEnumerable<T>, IReadOnlyList<T>, etc.
            if (type.IsGenericType)
            {
                var genericDef = type.GetGenericTypeDefinition();
                if (genericDef == typeof(IEnumerable<>) ||
                    genericDef == typeof(IReadOnlyList<>) ||
                    genericDef == typeof(IList<>) ||
                    genericDef == typeof(List<>) ||
                    genericDef == typeof(ICollection<>) ||
                    genericDef == typeof(IReadOnlyCollection<>))
                {
                    return type.GetGenericArguments()[0];
                }
            }

            // Check implemented interfaces
            foreach (var iface in type.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                {
                    return iface.GetGenericArguments()[0];
                }
            }

            return null;
        }

        (Type? KeyType, Type? ValueType) GetDictionaryTypes(Type type)
        {
            if (type.IsGenericType)
            {
                var genericDef = type.GetGenericTypeDefinition();
                if (genericDef == typeof(Dictionary<,>) ||
                    genericDef == typeof(IDictionary<,>) ||
                    genericDef == typeof(IReadOnlyDictionary<,>))
                {
                    var args = type.GetGenericArguments();
                    return (args[0], args[1]);
                }
            }

            foreach (var iface in type.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IDictionary<,>))
                {
                    var args = iface.GetGenericArguments();
                    return (args[0], args[1]);
                }
            }

            return (null, null);
        }

        bool IsEnumerableType(Type type)
        {
            if (type == typeof(string)) return false;
            return typeof(IEnumerable).IsAssignableFrom(type);
        }

        bool IsDictionaryType(Type type)
        {
            if (type.IsGenericType)
            {
                var genericDef = type.GetGenericTypeDefinition();
                if (genericDef == typeof(Dictionary<,>) ||
                    genericDef == typeof(IDictionary<,>) ||
                    genericDef == typeof(IReadOnlyDictionary<,>))
                {
                    return true;
                }
            }

            return type.GetInterfaces().Any(i => 
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
        }

        string addTypeSchema(Type type)
        {
            var elementType = GetElementType(type);
            if (elementType != null && type != typeof(string))
            {
                return addType(elementType);
            }

            var typeInfo = options.GetTypeInfo(type);

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
            var typeSpec = type.Name;
            typeSpecs[type] = typeSpec;

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

                if (IsDictionaryType(pType))
                {
                    var (keyType, valueType) = GetDictionaryTypes(pType);
                    pTypeSpec = $"{{ [key: {addType(keyType)}]: {addType(valueType)} }}";
                }
                else if (IsEnumerableType(pType))
                {
                    var elemType = GetElementType(pType);
                    addType(elemType);
                    pTypeSpec = $"{addType(elemType)}[]";
                }
                else
                {
                    pTypeSpec = addType(pType);
                }

                schema.Properties.Add(new PropertySchema(name, pTypeSpec, propertyDescription));
            }

            return typeSpec;
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
