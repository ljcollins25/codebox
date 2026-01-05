(Spec updated to replace Command/Action terminology with Verb, add Hermes Verb Executor architecture, registry-based dispatch, filesystem command expansion, auto-directory creation rule, console/interactive pathways, and retention notes from code example. See full updated spec content in document.)

## Project Structure (Normative)

The v0 implementation is expected to be organized into multiple C# projects with clear responsibility boundaries. This structure is **normative** and exists to constrain agent behavior, avoid accidental coupling, and make the system evolvable.

### Hermes.Core

**Purpose**: Pure execution and type system. No I/O, no CLI, no VS Code assumptions.

**Responsibilities**:

* `HermesVerbExecutor`
* Verb registry and dispatch
* `IVerb<TArgs, TResult>` interface
* `VerbEnvelope`, `VerbResult`
* All Verb argument/result C# types
* Schema derivation (`JsonSchemaBuilder`)
* YAML → JSON normalization (format-level only)

**Non-responsibilities**:

* No console handling
* No file system access beyond what is invoked via Verbs
* No process spawning
* No Git operations

---

### Hermes.Verbs

**Purpose**: Concrete implementations of Verbs that touch the host system.

**Responsibilities**:

* Filesystem Verbs (`fs.*`)
* Process execution (`proc.run`)
* System inspection (`sys.machineInfo`)
* Path normalization and safety checks
* Automatic directory creation rules

**Non-responsibilities**:

* No parsing or dispatch logic
* No CLI or interactive concerns
* No schema generation

---

### Hermes.Cli

**Purpose**: Console entry point and interactive/non-interactive user experience.

**Responsibilities**:

* Argument parsing and flags (`--interactive`, etc.)
* Prompt lifecycle and submission boundaries
* Reading YAML/JSON input from stdin or files
* Invoking `HermesVerbExecutor`
* Printing results to stdout
* Borrowing CLI patterns from `AzureDevops.Pipeline.Utilities/AzureDevops.Pipeline.Utilities`

**Non-responsibilities**:

* No business logic
* No Verb implementations
* No schema derivation

---

### Hermes.Server

**Purpose**: Long-running host process that exposes Hermes execution over a stable protocol.

**Responsibilities**:

* Hosting a persistent `HermesVerbExecutor` instance
* Exposing execution via a protocol (e.g. stdin/stdout, named pipes, HTTP, or VS Code extension transport)
* Managing workspace root and agent asset locations
* Translating inbound requests into Verb envelopes
* Returning serialized results and errors
* Lifecycle management (startup, shutdown, crash safety)

**Non-responsibilities**:

* No CLI argument parsing or user interaction UX
* No direct schema derivation logic
* No Verb business logic (delegated to `Hermes.Verbs`)

---

### Hermes.Tests

**Purpose**: Verification of semantics and regression safety.

**Responsibilities**:

* Unit tests for `HermesVerbExecutor`
* Golden tests for schema output (`JsonSchemaBuilder`)
* Filesystem Verb tests using temp directories
* Process execution tests (platform-gated where required)

**Non-responsibilities**:

* No mocks of core semantics
* No speculative behavior tests

---

## Verb Argument and Result Schemas (Normative)

This section defines the required input (Arguments) and output (Result) shapes for each Verb supported in v0. These schemas are *authoritative* and correspond 1:1 with the C# argument and result types. JSON/YAML schemas MUST be derived mechanically from these C# types using `System.Text.Json` metadata (`JsonTypeInfo`).

### General Rules

* Every Verb has exactly one **Arguments** type and one **Result** type.
* Arguments and Result types MUST be plain C# record/class types.
* Serialization MUST use `System.Text.Json` with camelCase naming.
* YAML input is first converted to JSON, then deserialized.
* No handwritten JSON schemas are permitted; schemas are derived from C# types only.

---

### fs.exists

**Arguments**

```csharp
public sealed class FsExistsArgs
{
    public required string Path { get; init; }
}
```

**Result**

```csharp
public sealed class FsExistsResult : VerbResult
{
    public required bool Exists { get; init; }
}
```

---

### fs.readFile

**Arguments**

```csharp
public sealed class FsReadFileArgs
{
    public required string Path { get; init; }
}
```

**Result**

```csharp
public sealed class FsReadFileResult : VerbResult
{
    public required string Content { get; init; }
}
```

---

### fs.readRange

**Arguments**

```csharp
public sealed class FsReadRangeArgs
{
    public required string Path { get; init; }
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
    public bool IncludeLineNumbers { get; init; } = true;
}
```

**Result**

```csharp
public sealed class FsReadRangeResult : VerbResult
{
    public required string Content { get; init; }
}
```

---

### fs.writeFile

**Arguments**

```csharp
public sealed class FsWriteFileArgs
{
    public required string Path { get; init; }
    public required string Content { get; init; }
}
```

**Result**

```csharp
public sealed class FsWriteFileResult : VerbResult
{
}
```

> Parent directories MUST be created automatically if missing.

---

### fs.writeRange

**Arguments**

```csharp
public sealed class FsWriteRangeArgs
{
    public required string Path { get; init; }
    public required int StartLine { get; init; }
    public int? EndLine { get; init; }
    public required string Content { get; init; }
}
```

**Result**

```csharp
public sealed class FsWriteRangeResult : VerbResult
{
}
```

> `EndLine` is inclusive. If `EndLine` is equal to `StartLine`, exactly one line is replaced.
>
> If `EndLine` is null, the content is inserted starting at `StartLine` without replacing an existing range.

---

### fs.deleteFile

**Arguments**

```csharp
public sealed class FsDeleteFileArgs
{
    public required string Path { get; init; }
}
```

**Result**

```csharp
public sealed class FsDeleteFileResult : VerbResult
{
}
```

---

### fs.moveFile / fs.copyFile

**Arguments**

```csharp
public sealed class FsMoveFileArgs
{
    public required string SourcePath { get; init; }
    public required string DestinationPath { get; init; }
}
```

**Result**

```csharp
public sealed class FsMoveFileResult : VerbResult
{
}
```

> Destination parent directories MUST be created automatically.

---

### fs.createDirectory

**Arguments**

```csharp
public sealed class FsCreateDirectoryArgs
{
    public required string Path { get; init; }
}
```

**Result**

```csharp
public sealed class FsCreateDirectoryResult : VerbResult
{
}
```

---

### fs.deleteDirectory

**Arguments**

```csharp
public sealed class FsDeleteDirectoryArgs
{
    public required string Path { get; init; }
}
```

**Result**

```csharp
public sealed class FsDeleteDirectoryResult : VerbResult
{
}
```

> Deletion is recursive by default.

---

### fs.lineCount

**Arguments**

```csharp
public sealed class FsLineCountArgs
{
    public required string Path { get; init; }
}
```

**Result**

```csharp
public sealed class FsLineCountResult : VerbResult
{
    public required int LineCount { get; init; }
}
```

---

### fs.listDir

**Arguments**

```csharp
public sealed class FsListDirArgs
{
    public required string Path { get; init; }
}
```

**Result**

```csharp
public sealed class FsListDirResult : VerbResult
{
    public required IReadOnlyList<DirEntry> Entries { get; init; }
}

public sealed class DirEntry
{
    public required string Name { get; init; }
    public required bool IsDirectory { get; init; }
}
```

---

### proc.run

**Arguments**

```csharp
public sealed class ProcRunArgs
{
    public required string Executable { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
}
```

**Result**

```csharp
public sealed class ProcRunResult : VerbResult
{
    public required int ExitCode { get; init; }
    public required string StdoutPath { get; init; }
    public required string StderrPath { get; init; }
}
```

---

### sys.machineInfo

**Arguments**

```csharp
public sealed class SysMachineInfoArgs
{
}
```

**Result**

```csharp
public sealed class SysMachineInfoResult : VerbResult
{
    public required string OperatingSystem { get; init; }
    public required int CpuCount { get; init; }
    public required long TotalMemoryBytes { get; init; }
    public required IReadOnlyList<DiskInfo> Disks { get; init; }
}

public sealed class DiskInfo
{
    public required string Name { get; init; }
    public required long TotalBytes { get; init; }
    public required long FreeBytes { get; init; }
}
```

---

## Schema Derivation Requirement

> **CLI Handling Note: Implementations SHOULD reuse or adapt CLI handling logic from the **AzureDevops.Pipeline.Utilities/AzureDevops.Pipeline.Utilities** repository (the same repo this tool is added to), rather than re‑inventing argument parsing, streaming, or console interaction patterns.

Implementations MUST derive argument and result schemas programmatically from the authoritative C# types using `System.Text.Json` metadata (`JsonTypeInfo`). Handwritten or manually-maintained JSON schema definitions are explicitly forbidden.

The purpose of schema derivation is to expose the *structural shape* of types (properties, requiredness, nullability, nesting), not to emit full standards-compliant JSON Schema.

### Normative Reference Implementation (Schema Projection)

The following code is provided as a **normative reference** for how schema derivation MUST be implemented. Implementations MAY refactor or reorganize this logic, but MUST preserve its observable behavior and semantics.

---

## Appendix: Reference Schema Generation Code (Non-JSON-Schema)

> **Note**: The following code defines a lightweight, descriptive IDL derived from `System.Text.Json` metadata.
> It is **not** JSON Schema and intentionally prioritizes readability and practical introspection.
>
> Only built-in attributes (e.g. `System.ComponentModel.DescriptionAttribute`) are used.
> No custom schema-description attributes are defined or supported.
>
> This code is included verbatim as the reference implementation for schema generation logic.

```csharp
// Reference implementation copied verbatim from discussion
// See source for full context and usage

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
                    pTypeSpec = $"{addType(pTypeInfo.ElementType)}[]";
                }
                else if (pTypeInfo.Kind == JsonTypeInfoKind.Dictionary)
                {
                    pTypeSpec = $"{{ [key: {addType(pTypeInfo.KeyType)}]: {addType(pTypeInfo.ElementType)} }}";
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
```

---

## Appendix: Reference Verb Executor Implementation

The following code provides a **reference implementation** of the Hermes Verb Executor. It defines the registry-based dispatch mechanism used to map Verb names to strongly-typed argument/result handlers. This implementation is **normative**: alternative implementations MAY refactor internally, but MUST preserve the observable behavior and semantics.

```csharp
public sealed class HermesVerbExecutor
{
    private readonly Dictionary<string, IVerbRegistration> _verbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _serializerOptions;

    public HermesVerbExecutor(JsonSerializerOptions serializerOptions)
    {
        _serializerOptions = serializerOptions;
    }

    public void Register<TArgs, TResult>(string verb, IVerb<TArgs, TResult> handler)
        where TResult : VerbResult
    {
        if (_verbs.ContainsKey(verb))
            throw new InvalidOperationException($"Verb '{verb}' is already registered.");

        _verbs[verb] = new VerbRegistration<TArgs, TResult>(verb, handler);
    }

    public string Execute(string input)
    {
        // Input is YAML or JSON; YAML is converted to JSON prior to this call
        var envelope = JsonSerializer.Deserialize<VerbEnvelope>(input, _serializerOptions)
            ?? throw new InvalidOperationException("Invalid Verb envelope.");

        if (!_verbs.TryGetValue(envelope.Verb, out var registration))
            throw new InvalidOperationException($"Unknown Verb '{envelope.Verb}'.");

        var result = registration.Execute(envelope.Arguments, _serializerOptions);
        return JsonSerializer.Serialize(result, _serializerOptions);
    }

    private interface IVerbRegistration
    {
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

        public VerbResult Execute(JsonElement args, JsonSerializerOptions options)
        {
            var typedArgs = args.Deserialize<TArgs>(options)
                ?? throw new InvalidOperationException($"Failed to deserialize arguments for Verb '{_verb}'.");

            return _handler.Execute(typedArgs);
        }
    }
}

public interface IVerb<TArgs, TResult>
    where TResult : VerbResult
{
    TResult Execute(TArgs args);
}

public sealed class VerbEnvelope
{
    public required string Verb { get; init; }
    public required JsonElement Arguments { get; init; }
}

public abstract class VerbResult
{
    public bool Succeeded { get; init; } = true;
    public string? ErrorMessage { get; init; }
}
```

This executor:

* Uses a string-to-handler registry keyed by Verb name
* Enforces exactly one Arguments and Result type per Verb
* Relies entirely on `System.Text.Json` for deserialization
* Is agnostic to transport (CLI, VS Code, HTTP, etc.)

All production implementations MUST conform to these semantics.
