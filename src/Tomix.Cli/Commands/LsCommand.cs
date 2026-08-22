using System.CommandLine;
using Spectre.Console;
using Tomix.App.Ls;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Models;
using Tomix.Core.Properties;

namespace Tomix.Cli.Commands;

internal sealed class LsCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    private readonly CliStateStore _state;

    public LsCommand(IReadOnlyList<IModelProvider> providers, CliStateStore state)
    {
        _providers = providers;
        _state = state;
    }

    public Command Build()
    {
        var pathArgument = new Argument<string?>("path-filter")
        {
            Description =
                "Object-path filter. Bare names match literally ('Sales', 'Sales/Measures'); container " +
                "keywords pivot ('Tables', 'Measures', 'Sales/Partitions'); '*' is a wildcard " +
                "('Sa*', '*/Amount'); quote names with spaces (\"'Net Sales'/'Sales Amount'\"); " +
                "inside quotes, '' is a literal apostrophe.",
            Arity = ArgumentArity.ZeroOrOne
        };

        var modelArgument = new Argument<string>("model")
        {
            Description = "Path to model (if not using --model)",
            Arity = ArgumentArity.ZeroOrOne
        };

        var typeOption = new Option<string?>("--type")
        {
            Description = "Filter by type: table, measure, column, calculatedcolumn, hierarchy, " +
                          "partition, relationship, role, perspective, culture, kpi, tablepermission, " +
                          "calendar, expression, function."
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

        var command = new Command("ls", "List model objects")
        {
            pathArgument,
            modelArgument,
            typeOption,
            pathsOnlyOption,
            noMultilineOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var firstValue = parseResult.GetValue(pathArgument);
            var secondValue = parseResult.GetValue(modelArgument);

            // Canonical order is `ls [path-filter] [model]`, matching `get <path> [model]`.
            // The legacy `ls <model> [path-filter]` order stays accepted: a first positional
            // that actually opens as a model is treated as the model.
            var firstIsModel = !string.IsNullOrWhiteSpace(firstValue)
                && _providers.Any(p => p.CanOpen(new ModelReference(firstValue)));

            // Resolved before --recent is applied, and passed to it: TryResolveModel rejects
            // --recent combined with an explicit model, and handing it only --model would let
            // the positional form slip past that guard -- `ls --recent 1 ./model` then exited 0
            // having silently ignored the --recent selection, while `-m ./model` was rejected.
            var positionalModel = firstIsModel ? firstValue : secondValue;
            // Blank-checked rather than ?? : the guard downstream tests IsNullOrWhiteSpace, so a
            // present-but-empty --model would otherwise win the coalesce and hide the positional.
            var explicitModel = GlobalOptions.ModelValue(parseResult) is { } m && !string.IsNullOrWhiteSpace(m)
                ? m
                : positionalModel;

            if (!RecentConnections.TryResolveModel(
                    parseResult,
                    explicitModel,
                    _state,
                    out var activeReference,
                    out var recentExit))
                return recentExit;
            var hasContextModel = !string.IsNullOrWhiteSpace(activeReference.Value);

            ModelReference reference;
            string? pathFilter;

            // --database rides along: a positional endpoint rebuilt without it would open the
            // server and then fail to resolve a catalog, or silently pick the only one there.
            var database = parseResult.GetValue(GlobalOptions.Database);

            if (firstIsModel)
            {
                reference = new ModelReference(firstValue!, database);
                pathFilter = secondValue;
            }
            else if (!string.IsNullOrWhiteSpace(secondValue))
            {
                reference = new ModelReference(secondValue, database);
                pathFilter = firstValue;
            }
            else if (hasContextModel)
            {
                reference = activeReference;
                pathFilter = firstValue;
            }
            else
            {
                reference = new ModelReference(firstValue ?? "");
                pathFilter = null;
            }
            var typeValue = parseResult.GetValue(typeOption);
            var pathsOnly = parseResult.GetValue(pathsOnlyOption);
            var noMultiline = parseResult.GetValue(noMultilineOption);
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            var errorFormat = GlobalOptions.ErrorFormatValue(parseResult, formatValue);

            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "ls", OutputFormats.Text, OutputFormats.Json, OutputFormats.Csv))
                return 2;

            ModelObjectKind? type = null;
            if (!string.IsNullOrWhiteSpace(typeValue))
            {
                if (!ModelObjectKindParser.TryParse(typeValue, out var parsed))
                {
                    return TypeValidation.WriteInvalidTypeError(GlobalOptions.ErrorFormatValue(parseResult, formatValue));
                }

                type = parsed;
            }

            var handler = new LsModelHandler(_providers);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var result = await CliSpinner.RunAsync(
                "Loading model...",
                () => handler.HandleAsync(
                    new LsModelRequest(reference, pathFilter, type),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(formatValue) || OutputFormats.IsCsv(formatValue));

            return CommandOutput.Render(
                result,
                formatValue,
                data => LsRenderer.Render(data, pathsOnly, noMultiline),
                ToReferenceJson,
                RenderCsv,
                errorFormat: errorFormat);
        });

        return command;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> ToReferenceJson(LsModelResult data)
        => data.Objects.Select(ToReferenceJson).ToList();

    private static IReadOnlyDictionary<string, object?> ToReferenceJson(LsObject obj)
    {
        var row = new Dictionary<string, object?>
        {
            ["type"] = obj.Kind.ToString(),
            ["path"] = obj.Path
        };
        foreach (var (key, value) in obj.Projected)
            row[key] = value;
        return row;
    }

    private static void RenderCsv(LsModelResult data)
    {
        var objects = data.Objects;

        // Homogeneous results get their kind's full catalog columns. Mixed kinds fall back to
        // the generic descriptors; their values come from LsObject's own fields because each
        // row's Projected dictionary is keyed by its OWN kind's catalog (a Column projection
        // has "dataType", not "detail").
        var homogeneous = objects.Count > 0 && objects.All(o => o.Kind == objects[0].Kind);

        PropertyCsvRenderer.Write(
            homogeneous ? ModelPropertyCatalog.For(objects[0].Kind) : ModelPropertyCatalog.GenericDescriptors,
            objects.Select(o => (
                (IReadOnlyList<object?>)[o.Path],
                homogeneous ? o.Projected : GenericProjection(o))),
            "Path");
    }

    private static IReadOnlyDictionary<string, object?> GenericProjection(LsObject obj)
        => new Dictionary<string, object?>
        {
            ["name"] = obj.Name,
            ["description"] = obj.Description ?? "",
            ["isHidden"] = obj.Hidden,
            ["detail"] = obj.Detail ?? "",
            ["expression"] = obj.Expression ?? ""
        };
}
