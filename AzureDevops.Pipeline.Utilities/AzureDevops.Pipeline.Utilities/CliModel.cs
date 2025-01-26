using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.IO;

namespace AzureDevops.Pipeline.Utilities;

public class CliModel
{
    public static Command Bind<T>(Command command, Func<CliModel<T>, T> getOptions, Func<T, Task<int>> runAsync)
    {
        var model = new CliModel<T>(command);
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

        return command;
    }
}

public class CliModel<T>(Command command)
{
    public bool OptionsMode { get; set; } = true;
    public IConsole Console { get; set; } = new SystemConsole();

    public delegate ref TField RefFunc<TField>(T model);

    private List<Action<T, InvocationContext>> SetFields { get; } = new();

    public void Apply(T target, InvocationContext context)
    {
        foreach (var item in SetFields)
        {
            item(target, context);
        }
    }


    public TField Option<TField>(RefFunc<TField> getFieldRef, string name, string? description = null, bool required = false, Optional<TField> defaultValue = default, bool isHidden = false)
    {
        if (OptionsMode)
        {
            name = name.StartsWith("--") ? name : $"--{name}";
            var option = defaultValue.HasValue
                ? new Option<TField>(name, getDefaultValue: () => defaultValue.Value!, description: description)
                : new Option<TField>(name, description: description);

            option.IsRequired = required;
            option.IsHidden = isHidden;

            SetFields.Add((model, context) =>
            {
                var result = context.ParseResult.FindResultFor(option);
                if (result != null)
                {
                    getFieldRef(model) = context.ParseResult.GetValueForOption(option)!;
                }
            });

            command.AddOption(option);
        }

        return default!;
    }
}
