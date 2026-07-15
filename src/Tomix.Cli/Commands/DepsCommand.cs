using System.CommandLine;
using Tomix.App.Deps;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Models;
using Spectre.Console;

namespace Tomix.Cli.Commands;

internal sealed class DepsCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public DepsCommand(IReadOnlyList<IModelProvider> providers) => _providers = providers;

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

            if (!CommandOutput.TryValidateFormat(formatValue, "deps", OutputFormats.Text, OutputFormats.Json))
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

            if (!RecentConnections.TryGetSource(
                    parseResult,
                    GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument),
                    out var source,
                    out var recentExit))
                return recentExit;

            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var result = await CliSpinner.RunAsync(
                "Analyzing dependencies...",
                () => new DepsModelHandler(_providers).HandleAsync(
                    new DepsModelRequest(
                        RecentConnections.CreateResolver(source).ResolveReference(source.Model, source.Database, source.Server),
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
                data => Render(data, showUpstream: !downstreamOnly, showDownstream: !upstreamOnly, deep: deep, quiet: quiet),
                data => ToReferenceJson(data, includeUpstream: !downstreamOnly, includeDownstream: !upstreamOnly),
                errorFormat: errorFormat);
        });

        return command;
    }

    private static void Render(DepsModelResult result, bool showUpstream, bool showDownstream, bool deep, bool quiet)
    {
        if (result.Unused is not null)
        {
            RenderUnused(result.Unused);
            return;
        }

        if (!quiet)
        {
            AnsiConsole.MarkupLine(Styling.Value("Running semantic analysis..."));
            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine(Styling.Title($"Dependencies for {result.Path} ({result.Type})"));
        if (showUpstream)
        {
            AnsiConsole.WriteLine();
            RenderSection("Upstream (depends on)", result.Upstream, deep);
        }

        if (showDownstream)
        {
            AnsiConsole.WriteLine();
            RenderSection("Downstream (referenced by)", result.Downstream, deep);
        }
    }

    private static void RenderSection(string title, IReadOnlyList<DependencyObject> dependencies, bool deep)
    {
        var header = $"{title}: {dependencies.Count}";
        if (dependencies.Count == 0)
        {
            AnsiConsole.MarkupLine(Styling.Bold($"{header}"));
            AnsiConsole.MarkupLine($"{Styling.Muted("None")}");
            return;
        }

        if (deep)
        {
            var tree = new Tree(Styling.Bold(header));
            AddTreeNodes(tree, dependencies);
            AnsiConsole.Write(tree);
            return;
        }

        AnsiConsole.MarkupLine(Styling.Bold($"{header}"));
        var table = Styling.NewTable("Type", "Reference", "Path");
        foreach (var dependency in dependencies)
            table.AddRow(
                Styling.MarkupEscape(dependency.Type),
                Styling.MarkupEscape(dependency.Reference),
                Styling.MarkupEscape(dependency.Path));
        AnsiConsole.Write(table);
    }

    private static void AddTreeNodes(IHasTreeNodes parent, IReadOnlyList<DependencyObject> dependencies)
    {
        foreach (var dependency in dependencies)
        {
            var node = parent.AddNode(
                $"{Styling.Value(dependency.Reference)} {Styling.Muted($"({dependency.Type})")}");
            AddTreeNodes(node, dependency.Children);
        }
    }

    private static void RenderUnused(IReadOnlyList<DependencyObject> unused)
    {
        AnsiConsole.MarkupLine(Styling.Title($"Unused objects: {unused.Count}"));
        AnsiConsole.WriteLine();
        if (unused.Count == 0)
        {
            AnsiConsole.MarkupLine($"{Styling.Muted("None")}");
            return;
        }

        var table = Styling.NewTable("Type", "Path");
        foreach (var dependency in unused)
            table.AddRow(
                Styling.MarkupEscape(dependency.Type),
                Styling.MarkupEscape(dependency.Path));
        AnsiConsole.Write(table);
    }

    private static object ToReferenceJson(
        DepsModelResult result,
        bool includeUpstream,
        bool includeDownstream)
    {
        if (result.Unused is not null)
            return new Dictionary<string, object?>
            {
                ["unused"] = result.Unused.Select(ToReferenceJson).ToList()
            };

        var json = new Dictionary<string, object?>
        {
            ["path"] = result.Path,
            ["objectType"] = result.Type
        };

        if (includeUpstream)
            json["upstream"] = result.Upstream.Select(ToReferenceJson).ToList();
        if (includeDownstream)
            json["downstream"] = result.Downstream.Select(ToReferenceJson).ToList();

        return json;
    }

    private static object ToReferenceJson(DependencyObject dependency)
    {
        var json = new Dictionary<string, object?>
        {
            ["objectName"] = dependency.Reference,
            ["objectType"] = dependency.Type,
            ["path"] = dependency.Path
        };

        if (dependency.Children.Count > 0)
            json["children"] = dependency.Children.Select(ToReferenceJson).ToList();

        return json;
    }
}
