using System.CommandLine;
using Tomix.App.Ls;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Models;
using Tomix.Core.Properties;
using Spectre.Console;

namespace Tomix.Cli.Commands;

internal sealed class LsCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public LsCommand(IReadOnlyList<IModelProvider> providers) => _providers = providers;

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
                          "partition, relationship, role, perspective, culture."
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
            var globalModel = GlobalOptions.ModelValue(parseResult);
            var server = parseResult.GetValue(GlobalOptions.Server);
            var database = parseResult.GetValue(GlobalOptions.Database);

            var activeReference = new ActiveModelResolver().ResolveReference(globalModel, database, server);
            var hasContextModel = !string.IsNullOrWhiteSpace(activeReference.Value);

            // Canonical order is `ls [path-filter] [model]`, matching `get <path> [model]`.
            // The legacy `ls <model> [path-filter]` order stays accepted: a first positional
            // that actually opens as a model is treated as the model.
            var firstIsModel = !string.IsNullOrWhiteSpace(firstValue)
                && _providers.Any(p => p.CanOpen(new ModelReference(firstValue)));

            ModelReference reference;
            string? pathFilter;

            if (firstIsModel)
            {
                reference = new ModelReference(firstValue!);
                pathFilter = secondValue;
            }
            else if (!string.IsNullOrWhiteSpace(secondValue))
            {
                reference = new ModelReference(secondValue);
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
            var errorFormat = parseResult.GetValue(GlobalOptions.ErrorFormat);

            if (!CommandOutput.TryValidateFormat(formatValue, "ls", OutputFormats.Text, OutputFormats.Json, OutputFormats.Csv))
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
