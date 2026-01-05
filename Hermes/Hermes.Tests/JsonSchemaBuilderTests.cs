using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Hermes.Core;
using Xunit;

namespace Hermes.Tests;

public class JsonSchemaBuilderTests
{
    private readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    [Fact]
    public void GetSchema_SimpleType_GeneratesSchema()
    {
        var schema = JsonSchemaBuilder.GetSchema(typeof(SimpleTestType), _options);

        Assert.Contains("type SimpleTestType", schema);
        Assert.Contains("name:", schema);
        Assert.Contains("value:", schema);
    }

    [Fact]
    public void GetSchema_RequiredProperty_NotOptional()
    {
        var schema = JsonSchemaBuilder.GetSchema(typeof(RequiredPropertyType), _options);

        // Required properties should NOT have the ? suffix
        Assert.Contains("name:", schema);
        Assert.DoesNotContain("name?:", schema);
    }

    [Fact]
    public void GetSchema_OptionalProperty_HasQuestionMark()
    {
        var schema = JsonSchemaBuilder.GetSchema(typeof(OptionalPropertyType), _options);

        // Optional properties should have the ? suffix
        Assert.Contains("name?:", schema);
    }

    [Fact]
    public void GetSchema_ArrayProperty_ShowsArraySyntax()
    {
        var schema = JsonSchemaBuilder.GetSchema(typeof(ArrayPropertyType), _options);

        Assert.Contains("String[]", schema);
    }

    [Fact]
    public void GetSchema_NestedType_IncludesNestedDefinition()
    {
        var schema = JsonSchemaBuilder.GetSchema(typeof(NestedParentType), _options);

        Assert.Contains("type NestedParentType", schema);
        Assert.Contains("type NestedChildType", schema);
    }
}

// Test types for schema generation
public sealed class SimpleTestType
{
    public required string Name { get; init; }
    public required int Value { get; init; }
}

public sealed class RequiredPropertyType
{
    public required string Name { get; init; }
}

public sealed class OptionalPropertyType
{
    public string? Name { get; init; }
}

public sealed class ArrayPropertyType
{
    public required IReadOnlyList<string> Items { get; init; }
}

public sealed class NestedParentType
{
    public required NestedChildType Child { get; init; }
}

public sealed class NestedChildType
{
    public required string Value { get; init; }
}
