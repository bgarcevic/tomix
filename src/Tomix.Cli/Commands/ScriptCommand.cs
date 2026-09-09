using System.CommandLine;
using Spectre.Console;
using Tomix.App.Mutations;
using Tomix.App.Script;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

internal sealed class ScriptCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    private readonly CliStateStore _state;
    private readonly MutationStores _mutations;

    public ScriptCommand(IReadOnlyList<IModelProvider> providers, CliStateStore state, MutationStores mutations)
    {
        _providers = providers;
        _state = state;
        _mutations = mutations;
    }

    public Command Build()
    {
        var modelArgument = new Argument<string>("model")
        {
            Description = "Path to model, Fabric path, or omit for active connection",
            Arity = ArgumentArity.ZeroOrOne
        };

        var scriptOption = new Option<string[]>("--script", "-S")
        {
            Description = "Path(s) to .cs or .csx script file(s). Can be repeated.",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = true,
            CustomParser = result => result.Tokens.Select(token => token.Value).ToArray()
        };

        var expressionOption = new Option<string[]>("--expression", "-e")
        {
            Description = "Inline C# expression(s) to execute. Use '-' to read from stdin.",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = true,
            CustomParser = result => result.Tokens.Select(token => token.Value).ToArray()
        };

        var saveToOption = LifecycleOptions.SaveTo("Save model to a different path after all scripts execute");

        var serializationOption = LifecycleOptions.Serialization();

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Compile script and report errors without executing"
        };

        var forceOption = LifecycleOptions.Force("Save even if this mutation introduces DAX validation errors");
        var overwriteOption = LifecycleOptions.Overwrite();

        var saveOption = LifecycleOptions.Save();

        var stageOption = LifecycleOptions.Stage();

        var revertOption = LifecycleOptions.Revert();

        var noSyncOption = LifecycleOptions.NoSync();

        var command = new Command("script", "Execute C# script(s) against a semantic model")
        {
            modelArgument,
            scriptOption,
            expressionOption,
            saveToOption,
            serializationOption,
            dryRunOption,
            forceOption,
            overwriteOption,
            saveOption,
            stageOption,
            revertOption,
            noSyncOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            var errorFormat = GlobalOptions.ErrorFormatValue(parseResult, formatValue);

            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "script", OutputFormats.Text, OutputFormats.Json, OutputFormats.Csv))
                return 2;

            var scriptValues = CollectRepeatedValues(parseResult, "-S", "--script");
            var expressionValues = CollectRepeatedValues(parseResult, "-e", "--expression");
            var scriptFiles = scriptValues.Count > 0
                ? scriptValues
                : parseResult.GetValue(scriptOption) ?? [];
            var expressions = ResolveExpressions(expressionValues.Count > 0
                ? expressionValues
                : parseResult.GetValue(expressionOption) ?? []);
            var explicitModel = GlobalOptions.ModelValue(parseResult)
                ?? parseResult.GetValue(modelArgument)
                ?? CollectModelArgument("script");
            if (!RecentConnections.TryResolveModel(
                    parseResult,
                    explicitModel,
                    _state,
                    out var model,
                    out var recentExit))
                return recentExit;

            // Only the persisting and discarding forms ask: --save overwrites the source (and
            // syncs the mirror) and --revert drops staged work; --save-to writes a copy and
            // --stage defers the gate to 'stage commit'.
            if (parseResult.GetValue(revertOption))
            {
                if (!ConfirmationHelper.ConfirmOrAbort(
                        "Revert staged changes", $"for {model.Value}", parseResult, formatValue))
                    return 1;
            }
            else if (parseResult.GetValue(saveOption) && !parseResult.GetValue(dryRunOption)
                && !ConfirmationHelper.ConfirmOrAbort(
                    "Save script changes", $"to {model.Value}", parseResult, formatValue))
            {
                return 1;
            }

            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var result = await CliSpinner.RunAsync(
                "Running script...",
                () => new ScriptHandler(_providers, _mutations).HandleAsync(
                    new ScriptRunRequest(
                        model,
                        scriptFiles,
                        expressions,
                        parseResult.GetValue(dryRunOption),
                        parseResult.GetValue(forceOption),
                        parseResult.GetValue(saveOption),
                        parseResult.GetValue(saveToOption),
                        parseResult.GetValue(serializationOption),
                        parseResult.GetValue(stageOption),
                        parseResult.GetValue(revertOption),
                        parseResult.GetValue(noSyncOption),
                        Overwrite: parseResult.GetValue(overwriteOption)),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(formatValue) || OutputFormats.IsCsv(formatValue));

            return CommandOutput.Render(
                result,
                formatValue,
                data => ScriptRenderer.RenderText(data, formatValue),
                ScriptRenderer.ToReferenceJson,
                renderCsv: data => ScriptRenderer.RenderText(data, OutputFormats.Csv),
                errorFormat: errorFormat);
        });

        return command;
    }

    private static IReadOnlyList<string> ResolveExpressions(IReadOnlyList<string> expressions)
        => expressions.Select(expression => InputValueResolver.Resolve(expression) ?? expression).ToList();

    private static IReadOnlyList<string> CollectRepeatedValues(ParseResult parseResult, params string[] optionNames)
    {
        var targets = new HashSet<string>(optionNames, StringComparer.Ordinal);
        return OrderedOptionTokens.ReadOptions(parseResult)
            .Where(token => token.Value is not null && targets.Contains(token.Option))
            .Select(token => token.Value!)
            .ToList();
    }

    private static string? CollectModelArgument(string commandName)
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var commandIndex = Array.FindIndex(args, arg => string.Equals(arg, commandName, StringComparison.Ordinal));
        if (commandIndex < 0)
            return null;

        for (var i = commandIndex + 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--recent" or "--recents")
            {
                // --recent has optional-value arity: only skip the next token when it is
                // the numeric index, not the positional model argument.
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out _))
                    i++;
                continue;
            }

            if (ValueOptions.Contains(arg))
            {
                i++;
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Contains('=', StringComparison.Ordinal))
                continue;

            if (FlagOptions.Contains(arg) || arg.StartsWith("-", StringComparison.Ordinal))
                continue;

            return arg;
        }

        return null;
    }

    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "-e",
        "--expression",
        "-S",
        "--script",
        "--save-to",
        "--serialization",
        "-m",
        "--model",
        "--output-format",
        "--error-format",
        "-s",
        "--server",
        "-d",
        "--database",
        "--auth"
    };

    private static readonly HashSet<string> FlagOptions = new(StringComparer.Ordinal)
    {
        "--dry-run",
        "--force",
        "--save",
        "--stage",
        "--revert",
        "--no-sync",
        "--debug",
        "--non-interactive"
    };

}
