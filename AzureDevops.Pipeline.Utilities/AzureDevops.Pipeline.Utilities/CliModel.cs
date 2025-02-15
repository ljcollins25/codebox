using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.IO;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.Services.Common.CommandLine;
using Microsoft.VisualStudio.Services.TestManagement.TestPlanning.WebApi;

namespace AzureDevops.Pipeline.Utilities;

public class CliModel
{
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
            var target = getOptions(model);
            model.Apply(target, context);
            context.ExitCode = await runAsync(target);
        });

        return model;
    }
}

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
        string name = null!,
        string? description = null,
        bool required = false,
        Optional<TField> defaultValue = default,
        bool isHidden = false,
        RefFunc<T, bool>? isExplicitRef = null)
    {
        if (OptionsMode)
        {
            name = name.StartsWith("--") ? name : $"--{name}";
            var option = defaultValue.HasValue
                ? new Option<TField>(name, getDefaultValue: () => defaultValue.Value!, description: description)
                : new Option<TField>(name, description: description);

            option.IsRequired = required;
            option.IsHidden = isHidden;
            option.AllowMultipleArgumentsPerToken = true;

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
}
