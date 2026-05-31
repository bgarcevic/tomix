using System.CommandLine;
using Mdl.App.Ls;
using Mdl.Cli.Output;
using Mdl.Core.Models;

namespace Mdl.Cli.Commands;

internal sealed class LsCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public LsCommand(IReadOnlyList<IModelProvider> providers) => _providers = providers;

    public Command Build()
    {
        var modelArgument = new Argument<string>("model")
        {
            Description = "Path to the semantic model folder."
        };

        var pathArgument = new Argument<string?>("path-filter")
        {
            Description =
                "Object-path filter. Bare names match literally ('Sales', 'Sales/Measures'); container " +
                "keywords pivot ('Tables', 'Measures', 'Sales/Partitions'); '*' is a wildcard " +
                "('Sa*', '*/Amount'); quote names with spaces (\"'Net Sales'/'Sales Amount'\").",
            Arity = ArgumentArity.ZeroOrOne
        };

        var typeOption = new Option<string?>("--type")
        {
            Description = "Filter by type: table, measure, column, hierarchy, partition, " +
                          "relationship, role, perspective, culture."
        };

        var pathsOnlyOption = new Option<bool>("--paths-only")
        {
            Description = "Output one object path per line, suitable for piping to other commands."
        };

        var noMultilineOption = new Option<bool>("--no-multiline")
        {
            Description = "Collapse multi-line cell content (e.g. measure expressions) to a single " +
                          "line and truncate. Text output only."
        };

        var format = OutputFormats.CreateOption();

        var command = new Command("ls", "List semantic model objects.")
        {
            modelArgument,
            pathArgument,
            typeOption,
            pathsOnlyOption,
            noMultilineOption,
            format
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var path = parseResult.GetValue(modelArgument) ?? "";
            var pathFilter = parseResult.GetValue(pathArgument);
            var typeValue = parseResult.GetValue(typeOption);
            var pathsOnly = parseResult.GetValue(pathsOnlyOption);
            var noMultiline = parseResult.GetValue(noMultilineOption);
            var formatValue = parseResult.GetValue(format) ?? OutputFormats.Human;

            if (!CommandOutput.TryValidateFormat(formatValue))
                return 2;

            ModelObjectKind? type = null;
            if (!string.IsNullOrWhiteSpace(typeValue))
            {
                if (!TryParseType(typeValue, out var parsed))
                {
                    Console.Error.WriteLine(
                        "Invalid --type value. Expected: table, measure, column, hierarchy, " +
                        "partition, relationship, role, perspective, culture.");
                    return 2;
                }

                type = parsed;
            }

            var handler = new LsModelHandler(_providers);
            var result = await handler.HandleAsync(
                new LsModelRequest(new ModelReference(path), pathFilter, type),
                cancellationToken);

            return CommandOutput.Render(result, formatValue, data => LsRenderer.Render(data, pathsOnly, noMultiline));
        });

        return command;
    }

    private static bool TryParseType(string value, out ModelObjectKind kind)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "table": kind = ModelObjectKind.Table; return true;
            case "measure": kind = ModelObjectKind.Measure; return true;
            case "column": kind = ModelObjectKind.Column; return true;
            case "hierarchy": kind = ModelObjectKind.Hierarchy; return true;
            case "partition": kind = ModelObjectKind.Partition; return true;
            case "relationship": kind = ModelObjectKind.Relationship; return true;
            case "role": kind = ModelObjectKind.Role; return true;
            case "perspective": kind = ModelObjectKind.Perspective; return true;
            case "culture": kind = ModelObjectKind.Culture; return true;
            default: kind = default; return false;
        }
    }
}
