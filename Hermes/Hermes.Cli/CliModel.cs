using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.IO;
using System.CommandLine.Parsing;

namespace Hermes.Cli;

/// <summary>
/// Represents a CLI alias for a command.
/// </summary>
public record struct CliAlias(string Alias);

/// <summary>
/// Helper methods for CLI model binding.
/// </summary>
public static class CliModel
{
    /// <summary>
    /// Adds an alias to an identifier symbol.
    /// </summary>
    public static void Add(this IdentifierSymbol command, CliAlias alias)
    {
        command.AddAlias(alias.Alias);
    }

    /// <summary>
    /// Adds multiple options to a command.
    /// </summary>
    public static void Add(this Command command, IEnumerable<Option> options)
    {
        foreach (var option in options)
        {
            command.Add(option);
        }
    }

    /// <summary>
    /// Binds a command to an operation type with strongly-typed options.
    /// </summary>
    public static CliModel<T> Bind<T>(Command command, Func<CliModel<T>, T> getOptions, Func<T, Task<int>> runAsync)
    {
        var model = new CliModel<T>(command, (model, context) =>
        {
            var target = getOptions(model);
            model.Apply(target, context);
            return target;
        });

        getOptions(model);

        // Disable options mode so that real target values get created in handler
        model.OptionsMode = false;

        command.SetHandler(async context =>
        {
            model.Console = context.Console ?? model.Console;
            var target = model.Create(context);
            context.ExitCode = await runAsync(target);
        });

        return model;
    }
}

/// <summary>
/// Delegate for getting a reference to a field in a model.
/// </summary>
public delegate ref TField RefFunc<in T, TField>(T model);

/// <summary>
/// Strongly-typed CLI model for command binding.
/// </summary>
public record CliModel<T>(Command Command, Func<CliModel<T>, InvocationContext, T> CreateValue)
{
    /// <summary>
    /// Whether the model is in options definition mode.
    /// </summary>
    public bool OptionsMode { get; set; } = true;

    /// <summary>
    /// The console for output.
    /// </summary>
    public IConsole Console { get; set; } = new SystemConsole();

    /// <summary>
    /// Cancellation token for the operation.
    /// </summary>
    public CancellationToken Token { get; set; } = default;

    private List<Action<T, InvocationContext>> SetFields { get; } = new();

    /// <summary>
    /// Applies parsed values to the target.
    /// </summary>
    public void Apply(T target, InvocationContext context)
    {
        foreach (var item in SetFields)
        {
            item(target, context);
        }
    }

    /// <summary>
    /// Creates the target instance from the invocation context.
    /// </summary>
    public T Create(InvocationContext context)
    {
        Token = context.GetCancellationToken();
        return CreateValue(this, context);
    }

    /// <summary>
    /// Implicit conversion to Command.
    /// </summary>
    public static implicit operator Command(CliModel<T> model) => model.Command;

    /// <summary>
    /// Defines an option for the command.
    /// </summary>
    public TField Option<TField>(
        RefFunc<T, TField> getFieldRef,
        string name,
        string? description = null,
        bool required = false,
        Optional<TField> defaultValue = default,
        bool isHidden = false,
        RefFunc<T, bool>? isExplicitRef = null,
        ParseArgument<TField>? parse = null,
        string[]? aliases = null)
    {
        if (OptionsMode)
        {
            name = name.StartsWith("--") ? name : $"--{name}";

            var option = parse != null
                ? new Option<TField>(name, parseArgument: argResult => parse!(argResult),
                    isDefault: defaultValue.HasValue,
                    description: description)
                : defaultValue.HasValue
                ? new Option<TField>(name, getDefaultValue: () => defaultValue.Value!, description: description)
                : new Option<TField>(name, description: description);

            option.IsRequired = required;
            option.IsHidden = isHidden;
            option.AllowMultipleArgumentsPerToken = true;

            if (aliases != null)
            {
                foreach (var alias in aliases)
                {
                    option.AddAlias(alias.StartsWith("-") ? alias : $"--{alias}");
                }
            }

            SetFields.Add((model, context) =>
            {
                var result = context.ParseResult.FindResultFor(option);
                if (result != null)
                {
                    getFieldRef(model) = context.ParseResult.GetValueForOption(option)!;

                    if (isExplicitRef != null)
                    {
                        isExplicitRef(model) = !result.IsImplicit;
                    }
                }
            });

            Command.AddOption(option);
        }

        return default!;
    }

    /// <summary>
    /// Defines an argument for the command.
    /// </summary>
    public TField Argument<TField>(
        RefFunc<T, TField> getFieldRef,
        string name,
        string? description = null,
        ArgumentArity? arity = null,
        Optional<TField> defaultValue = default,
        bool isHidden = false,
        RefFunc<T, bool>? isExplicitRef = null,
        ParseArgument<TField>? parse = null)
    {
        if (OptionsMode)
        {
            var argument = parse != null
                ? new Argument<TField>(name, parse: argResult => parse!(argResult),
                    isDefault: defaultValue.HasValue,
                    description: description)
                : defaultValue.HasValue
                ? new Argument<TField>(name, getDefaultValue: () => defaultValue.Value!, description: description)
                : new Argument<TField>(name, description: description);

            argument.Arity = arity ?? ArgumentArity.ExactlyOne;
            argument.IsHidden = isHidden;

            SetFields.Add((model, context) =>
            {
                var result = context.ParseResult.FindResultFor(argument);
                if (result != null)
                {
                    getFieldRef(model) = context.ParseResult.GetValueForArgument(argument)!;

                    if (isExplicitRef != null)
                    {
                        isExplicitRef(model) = result.Tokens.Count != 0;
                    }
                }
            });

            Command.Add(argument);
        }

        return default!;
    }
}

/// <summary>
/// Represents an optional value that may or may not be present.
/// </summary>
public readonly struct Optional<T>
{
    private readonly T? _value;

    /// <summary>
    /// Whether the optional has a value.
    /// </summary>
    public bool HasValue { get; }

    /// <summary>
    /// The value, if present.
    /// </summary>
    public T? Value => HasValue ? _value : default;

    private Optional(T value)
    {
        _value = value;
        HasValue = true;
    }

    /// <summary>
    /// Implicit conversion from a value to an Optional.
    /// </summary>
    public static implicit operator Optional<T>(T value) => new(value);
}
