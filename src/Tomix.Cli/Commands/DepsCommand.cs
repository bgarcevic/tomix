using System.CommandLine;
using Spectre.Console;
using Tomix.App.Deps;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

internal sealed class DepsCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    private readonly CliStateStore _state;

    public DepsCommand(IReadOnlyList<IModelProvider> providers, CliStateStore state)
    {
        _providers = providers;
        _state = state;
    }

    public Command Build()
    {
        var pathArgument = new Argument<string>("path")
        {
            Description = "Object path to analyze. Slash-separated: 'Sales/Revenue'. DAX forms also accepted.",
            Arity = ArgumentArity.ZeroOrOne
        };
        var modelArgument = new Argument<string>("model")
        {
            Description = "Path to model (if not using --model)",
            Arity = ArgumentArity.ZeroOrOne
        };
        var upstreamOption = new Option<bool>("--upstream")
        {
            Description = "Show only upstream dependencies (what this object uses)"
        };
        var downstreamOption = new Option<bool>("--downstream")
        {
            Description = "Show only downstream dependents (what uses this object)"
        };
        var deepOption = new Option<bool>("--deep")
        {
            Description = "Show recursive dependency tree"
        };
        var unusedOption = new Option<bool>("--unused")
        {
            Description = "Find unreferenced measures and columns"
        };
        var hiddenOption = new Option<bool>("--hidden")
        {
            Description = "With --unused: only list unused objects whose IsHidden is true"
        };
        var maxDepthOption = new Option<int>("--max-depth")
        {
            Description = "Maximum depth for --deep traversal (default: 10)",
            DefaultValueFactory = _ => 10
        };
        maxDepthOption.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int>() < 1)
                result.AddError("--max-depth must be at least 1.");
        });
        var typeOption = new Option<string?>("--type")
        {
            Description = "Disambiguate when the path matches multiple table-children."
        };
        typeOption.Aliases.Add("-t");

        var command = new Command("deps", "Analyze object dependencies (upstream/downstream)")
        {
            pathArgument,
            modelArgument,
            upstreamOption,
            downstreamOption,
            deepOption,
            unusedOption,
            hiddenOption,
            maxDepthOption,
            typeOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            var errorFormat = parseResult.GetValue(GlobalOptions.ErrorFormat);
            var typeValue = parseResult.GetValue(typeOption);
            var upstreamRequested = parseResult.GetValue(upstreamOption);
            var downstreamRequested = parseResult.GetValue(downstreamOption);
            var upstreamOnly = upstreamRequested && !downstreamRequested;
            var downstreamOnly = downstreamRequested && !upstreamRequested;
            var deep = parseResult.GetValue(deepOption);

            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "deps", OutputFormats.Text, OutputFormats.Json))
                return 2;

            ModelObjectKind? type = null;
            if (!string.IsNullOrWhiteSpace(typeValue))
            {
                if (!ModelObjectKindParser.TryParse(typeValue, out var parsed))
                {
                    return TypeValidation.WriteInvalidTypeError();
                }

                type = parsed;
            }

            if (!RecentConnections.TryResolveModel(
                    parseResult,
                    GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument),
                    _state,
                    out var model,
                    out var recentExit))
                return recentExit;

            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var result = await CliSpinner.RunAsync(
                "Analyzing dependencies...",
                () => new DepsModelHandler(_providers).HandleAsync(
                    new DepsModelRequest(
                        model,
                        parseResult.GetValue(pathArgument),
                        type,
                        upstreamOnly,
                        downstreamOnly,
                        parseResult.GetValue(deepOption),
                        parseResult.GetValue(unusedOption),
                        parseResult.GetValue(hiddenOption),
                        parseResult.GetValue(maxDepthOption)),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(formatValue) || OutputFormats.IsCsv(formatValue));

            if (result.Data is null && OutputFormats.IsTextLike(formatValue) && !quiet)
            {
                AnsiConsole.MarkupLine(Styling.Value("Running semantic analysis..."));
                AnsiConsole.WriteLine();
            }

            return CommandOutput.Render(
                result,
                formatValue,
                data => DepsRenderer.Render(data, showUpstream: !downstreamOnly, showDownstream: !upstreamOnly, deep: deep, quiet: quiet),
                data => DepsRenderer.ToReferenceJson(data, includeUpstream: !downstreamOnly, includeDownstream: !upstreamOnly),
                errorFormat: errorFormat);
        });

        return command;
    }

}
