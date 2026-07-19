using System.CommandLine;
using Spectre.Console;
using Tomix.App;
using Tomix.App.Script;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

internal sealed class ScriptCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    private readonly AppServices _services;

    public ScriptCommand(IReadOnlyList<IModelProvider> providers, AppServices services)
    {
        _providers = providers;
        _services = services;
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

        var saveToOption = new Option<string?>("--save-to")
        {
            Description = "Save model to a different path after all scripts execute"
        };

        var serializationOption = new Option<string?>("--serialization")
        {
            Description = "Model serialization: tmdl, bim (tmsl and auto also accepted)"
        };
        serializationOption.AcceptAmongIgnoreCase("tmdl", "bim", "tmsl", "auto");

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Compile script and report errors without executing"
        };

        var forceOption = new Option<bool>("--force")
        {
            Description = "Save even if this mutation introduces DAX validation errors"
        };

        var saveOption = new Option<bool>("--save")
        {
            Description = "Persist this command's mutation to the source location. Mutually exclusive with --revert and --stage."
        };

        var stageOption = new Option<bool>("--stage")
        {
            Description = "Stage this command's mutation"
        };

        var revertOption = new Option<bool>("--revert")
        {
            Description = "Revert a staged mutation"
        };

        var noSyncOption = new Option<bool>("--no-sync")
        {
            Description = "Skip workspace sync when workspace mode is active."
        };

        var command = new Command("script", "Execute C# script(s) against a semantic model")
        {
            modelArgument,
            scriptOption,
            expressionOption,
            saveToOption,
            serializationOption,
            dryRunOption,
            forceOption,
            saveOption,
            stageOption,
            revertOption,
            noSyncOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            var errorFormat = parseResult.GetValue(GlobalOptions.ErrorFormat);

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
                    _services.State,
                    out var model,
                    out var recentExit))
                return recentExit;
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var result = await CliSpinner.RunAsync(
                "Running script...",
                () => new ScriptHandler(_providers, _services.Mutations).HandleAsync(
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
                        parseResult.GetValue(noSyncOption)),
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
        "--local",
        "--debug",
        "--non-interactive"
    };

}
