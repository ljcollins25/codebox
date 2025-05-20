using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.IO;
using System.CommandLine.Parsing;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.Services.TestManagement.TestPlanning.WebApi;

namespace Nexis.Azure.Utilities;

public record struct CliAlias(string Alias);

public static class CliModel
{
    public static void Add(this IdentifierSymbol command, CliAlias alias)
    {
        command.AddAlias(alias.Alias);
    }

    public static void Add(this Command command, IEnumerable<Option> options)
    {
        foreach (var option in options)
        {
            command.Add(option);
        }
    }

    public static CliModel<T> Bind<T>(Command command, Func<CliModel<T>, T> getOptions, Func<T, Task<int>> runAsync, ICliModel<T>? parent = null)
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

//public class ArgumentConverter<T>
//{
//    public static implicit operator ArgumentConverter<T>(Func<string, T> singleArgConverter)
//    {

//    }

//    public static implicit operator ArgumentConverter<T>(Func<List<string>, T> singleArgConverter)
//    {

//    }

//    public static implicit operator ArgumentConverter<T>(Func<List<string>, T> singleArgConverter)
//    {

//    }
//}

public delegate ref TField RefFunc<in T, TField>(T model);

public interface ICliModel<T>
{
    Command Command { get; }

    void Apply(T target, InvocationContext context);

    T Create(ref T target);
}

public record CliModel<T>(Command Command, Func<CliModel<T>, InvocationContext, T> CreateValue)
{
    public bool OptionsMode { get; set; } = true;
    public IConsole Console { get; set; } = new SystemConsole();
    public CancellationToken Token { get; set; } = default;

    private List<Action<T, InvocationContext>> SetFields { get; } = new();

    public void Apply(T target, InvocationContext context)
    {
        foreach (var item in SetFields)
        {
            item(target, context);
        }
    }

    public T Create(InvocationContext context)
    {
        Token = context.GetCancellationToken();
        return CreateValue(this, context);
    }

    public static implicit operator Command(CliModel<T> model) => model.Command;

    public TField SharedOptions<TField>(RefFunc<T, TField> getFieldRef, CliModel<TField> sharedModel)
    {
        if (OptionsMode)
        {
            SetFields.Add((model, context) =>
            {
                var value = sharedModel.Create(context);
                getFieldRef(model) = value;
            });

            foreach (var option in sharedModel.Command.Options)
            {
                Command.Add(option);
            }
        }

        return default!;
    }

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

            var option =  parse != null
                ? new Option<TField>(name, parseArgument: argResult =>
                    {
                        if (defaultValue.HasValue)
                        {

                        }

                        return parse!(argResult);
                    },
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
                    option.AddAlias(alias);
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
            var option = parse != null
                ? new Argument<TField>(name, parse: argResult =>
                    {
                        if (defaultValue.HasValue)
                        {

                        }

                        return parse!(argResult);
                    },
                    isDefault: defaultValue.HasValue,
                    description: description)
                : defaultValue.HasValue
                ? new Argument<TField>(name, getDefaultValue: () => defaultValue.Value!, description: description)
                : new Argument<TField>(name, description: description);

            option.Arity = arity ?? ArgumentArity.ExactlyOne;
            option.IsHidden = isHidden;

            SetFields.Add((model, context) =>
            {
                var result = context.ParseResult.FindResultFor(option);
                if (result != null)
                {
                    getFieldRef(model) = context.ParseResult.GetValueForArgument(option)!;

                    if (isExplicitRef != null)
                    {
                        isExplicitRef(model) = result.Children.Count != 0;
                    }
                }
            });

            Command.Add(option);
        }

        return default!;
    }
}
