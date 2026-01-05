using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hermes.Core;
using Hermes.Verbs;

namespace Hermes.Cli;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Hermes VeRB Executor CLI");

        var executeCommand = new Command("execute", "Execute a VeRB from input");
        var inputOption = new Option<FileInfo?>(
            aliases: ["--input", "-i"],
            description: "Input file containing VeRB envelope (YAML or JSON). If not specified, reads from stdin.");
        var outputOption = new Option<FileInfo?>(
            aliases: ["--output", "-o"],
            description: "Output file for result (JSON). If not specified, writes to stdout.");
        var interactiveOption = new Option<bool>(
            aliases: ["--interactive"],
            description: "Run in interactive mode, processing multiple VeRBs.");
        var outputDirOption = new Option<DirectoryInfo>(
            aliases: ["--output-dir", "-d"],
            getDefaultValue: () => new DirectoryInfo(Path.Combine(Path.GetTempPath(), "hermes")),
            description: "Directory for process output files.");

        executeCommand.AddOption(inputOption);
        executeCommand.AddOption(outputOption);
        executeCommand.AddOption(interactiveOption);
        executeCommand.AddOption(outputDirOption);

        executeCommand.SetHandler(ExecuteHandler, inputOption, outputOption, interactiveOption, outputDirOption);

        var listCommand = new Command("list", "List all registered VeRBs");
        listCommand.SetHandler(ListHandler);

        var schemaCommand = new Command("schema", "Get schema for a VeRB");
        var verbArgument = new Argument<string>("verb", "The VeRB name to get schema for");
        schemaCommand.AddArgument(verbArgument);
        schemaCommand.SetHandler(SchemaHandler, verbArgument);

        rootCommand.AddCommand(executeCommand);
        rootCommand.AddCommand(listCommand);
        rootCommand.AddCommand(schemaCommand);

        return await rootCommand.InvokeAsync(args);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static void ExecuteHandler(FileInfo? input, FileInfo? output, bool interactive, DirectoryInfo outputDir)
    {
        var executor = CreateExecutor(outputDir.FullName);

        if (interactive)
        {
            RunInteractive(executor, output);
        }
        else
        {
            RunSingle(executor, input, output);
        }
    }

    private static void RunSingle(HermesVerbExecutor executor, FileInfo? input, FileInfo? output)
    {
        string inputText;
        
        if (input != null)
        {
            inputText = File.ReadAllText(input.FullName);
        }
        else
        {
            inputText = Console.In.ReadToEnd();
        }

        var json = YamlToJsonConverter.NormalizeToJson(inputText);
        
        try
        {
            var result = executor.Execute(json);
            WriteOutput(result, output);
        }
        catch (Exception ex)
        {
            var errorResult = JsonSerializer.Serialize(new
            {
                succeeded = false,
                errorMessage = ex.Message
            }, SerializerOptions);
            WriteOutput(errorResult, output);
            Environment.ExitCode = 1;
        }
    }

    private static void RunInteractive(HermesVerbExecutor executor, FileInfo? output)
    {
        Console.Error.WriteLine("Hermes Interactive Mode. Enter VeRB envelopes (YAML/JSON), followed by an empty line to execute.");
        Console.Error.WriteLine("Type 'exit' or Ctrl+C to quit.");

        while (true)
        {
            Console.Error.Write("> ");
            var lines = new List<string>();
            string? line;

            while ((line = Console.ReadLine()) != null)
            {
                if (line.Equals("exit", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(line) && lines.Count > 0)
                {
                    break;
                }

                lines.Add(line);
            }

            if (line == null)
            {
                // EOF
                break;
            }

            if (lines.Count == 0)
            {
                continue;
            }

            var inputText = string.Join(Environment.NewLine, lines);
            var json = YamlToJsonConverter.NormalizeToJson(inputText);

            try
            {
                var result = executor.Execute(json);
                WriteOutput(result, output);
            }
            catch (Exception ex)
            {
                var errorResult = JsonSerializer.Serialize(new
                {
                    succeeded = false,
                    errorMessage = ex.Message
                }, SerializerOptions);
                WriteOutput(errorResult, output);
            }
        }
    }

    private static void WriteOutput(string result, FileInfo? output)
    {
        if (output != null)
        {
            File.WriteAllText(output.FullName, result);
        }
        else
        {
            Console.WriteLine(result);
        }
    }

    private static void ListHandler()
    {
        var executor = CreateExecutor(Path.GetTempPath());
        var verbs = executor.GetRegistrations()
            .Select(r => r.Name)
            .OrderBy(v => v);

        foreach (var verb in verbs)
        {
            Console.WriteLine(verb);
        }
    }

    private static void SchemaHandler(string verb)
    {
        var executor = CreateExecutor(Path.GetTempPath());
        var registration = executor.GetRegistration(verb);

        if (registration == null)
        {
            Console.Error.WriteLine($"Unknown VeRB: {verb}");
            Environment.ExitCode = 1;
            return;
        }

        Console.WriteLine($"// VeRB: {verb}");
        Console.WriteLine();
        Console.WriteLine("// Arguments:");
        Console.WriteLine(JsonSchemaBuilder.GetSchema(registration.ArgumentType, SerializerOptions));
        Console.WriteLine();
        Console.WriteLine("// Result:");
        Console.WriteLine(JsonSchemaBuilder.GetSchema(registration.ResultType, SerializerOptions));
    }

    private static HermesVerbExecutor CreateExecutor(string outputDirectory)
    {
        var executor = new HermesVerbExecutor(SerializerOptions);
        VerbRegistration.RegisterAll(executor, outputDirectory);
        return executor;
    }
}
