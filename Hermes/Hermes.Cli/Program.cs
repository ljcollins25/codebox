using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hermes.Core;
using Hermes.Verbs;

namespace Hermes.Cli;

public class Program
{
    public static async Task<int> Main(params string[] args)
    {
        var rootCommand = GetCommand();

        var builder = new CommandLineBuilder(rootCommand)
            .UseVersionOption()
            .UseHelp()
            .UseEnvironmentVariableDirective()
            .UseParseDirective()
            .UseParseErrorReporting()
            .UseExceptionHandler()
            .CancelOnProcessTermination();

        return await builder.Build().InvokeAsync(args);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static RootCommand GetCommand()
    {
        return new RootCommand("Hermes Verb Executor CLI")
        {
            CliModel.Bind<ExecuteOperation>(
                new Command("execute", "Execute a Verb from input"),
                m =>
                {
                    var result = new ExecuteOperation
                    {
                        InputText = m.Argument(c => ref c.InputText, name: "input-text",
                            arity: System.CommandLine.ArgumentArity.ZeroOrOne,
                            description: "Verb envelope as YAML or JSON text. If not specified, reads from --input file or stdin."),

                        Input = m.Option(c => ref c.Input, name: "input", aliases: ["-i"],
                            description: "Input file containing Verb envelope (YAML or JSON). If not specified, reads from stdin."),

                        Output = m.Option(c => ref c.Output, name: "output", aliases: ["-o"],
                            description: "Output file for result (JSON). If not specified, writes to stdout."),

                        Interactive = m.Option(c => ref c.Interactive, name: "interactive",
                            description: "Run in interactive mode, processing multiple Verbs."),

                        OutputDirectory = m.Option(c => ref c.OutputDirectory, name: "output-dir", aliases: ["-d"],
                            defaultValue: Path.Combine(Path.GetTempPath(), "hermes"),
                            description: "Directory for process output files.")
                    };

                    return result;
                },
                r => r.RunAsync()),

            CliModel.Bind<ListOperation>(
                new Command("list", "List all registered Verbs"),
                m => new ListOperation(),
                r => r.RunAsync()),

            CliModel.Bind<SchemaOperation>(
                new Command("schema", "Get schema for a Verb"),
                m =>
                {
                    var result = new SchemaOperation
                    {
                        Verb = m.Argument(c => ref c.Verb, name: "verb",
                            description: "The Verb name to get schema for")
                    };

                    return result;
                },
                r => r.RunAsync())
        };
    }

    /// <summary>
    /// Execute operation - runs Verb(s) from input.
    /// </summary>
    private class ExecuteOperation
    {
        public string? InputText;
        public FileInfo? Input;
        public FileInfo? Output;
        public bool Interactive;
        public string OutputDirectory = Path.Combine(Path.GetTempPath(), "hermes");

        public Task<int> RunAsync()
        {
            var executor = CreateExecutor(OutputDirectory);

            if (Interactive)
            {
                RunInteractive(executor, Output);
            }
            else
            {
                RunSingle(executor, InputText, Input, Output);
            }

            return Task.FromResult(Environment.ExitCode);
        }

        private static void RunSingle(HermesVerbExecutor executor, string? inputText, FileInfo? input, FileInfo? output)
        {
            if (string.IsNullOrEmpty(inputText))
            {
                if (input != null)
                {
                    inputText = File.ReadAllText(input.FullName);
                }
                else
                {
                    inputText = Console.In.ReadToEnd();
                }
            }

            try
            {
                var result = executor.Execute(inputText);
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
            Console.Error.WriteLine("Hermes Interactive Mode. Enter Verb envelopes (YAML/JSON), followed by an empty line to execute.");
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

                try
                {
                    var result = executor.Execute(inputText);
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
    }

    /// <summary>
    /// List operation - lists all registered Verbs.
    /// </summary>
    private class ListOperation
    {
        public Task<int> RunAsync()
        {
            var executor = CreateExecutor(Path.GetTempPath());
            var verbs = executor.GetRegistrations()
                .Select(r => r.Name)
                .OrderBy(v => v);

            foreach (var verb in verbs)
            {
                Console.WriteLine(verb);
            }

            return Task.FromResult(0);
        }
    }

    /// <summary>
    /// Schema operation - displays schema for a Verb.
    /// </summary>
    private class SchemaOperation
    {
        public string Verb = string.Empty;

        public Task<int> RunAsync()
        {
            var executor = CreateExecutor(Path.GetTempPath());
            var registration = executor.GetRegistration(Verb);

            if (registration == null)
            {
                Console.Error.WriteLine($"Unknown Verb: {Verb}");
                return Task.FromResult(1);
            }

            Console.WriteLine($"// Verb: {Verb}");
            Console.WriteLine();
            Console.WriteLine("// Arguments:");
            Console.WriteLine(JsonSchemaBuilder.GetSchema(registration.ArgumentType, SerializerOptions));
            Console.WriteLine();
            Console.WriteLine("// Result:");
            Console.WriteLine(JsonSchemaBuilder.GetSchema(registration.ResultType, SerializerOptions));

            return Task.FromResult(0);
        }
    }

    private static HermesVerbExecutor CreateExecutor(string outputDirectory)
    {
        var executor = new HermesVerbExecutor(SerializerOptions);
        VerbRegistration.RegisterAll(executor, outputDirectory);
        return executor;
    }
}
