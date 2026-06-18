using System.CommandLine;
using Tomix.App.Ls;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Models;
using Spectre.Console;

namespace Tomix.Cli.Commands;

internal sealed class LsCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public LsCommand(IReadOnlyList<IModelProvider> providers) => _providers = providers;

    public Command Build()
    {
        var modelArgument = new Argument<string>("model")
        {
            Description = "Path to model (if not using --model)",
            Arity = ArgumentArity.ZeroOrOne
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

        var command = new Command("ls", "List model objects")
        {
            modelArgument,
            pathArgument,
            typeOption,
            pathsOnlyOption,
            noMultilineOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var modelValue = parseResult.GetValue(modelArgument);
            var globalModel = GlobalOptions.ModelValue(parseResult);
            var server = parseResult.GetValue(GlobalOptions.Server);
            var database = parseResult.GetValue(GlobalOptions.Database);

            var activeReference = new ActiveModelResolver().ResolveReference(globalModel, database, server);
            var hasContextModel = !string.IsNullOrWhiteSpace(activeReference.Value);

            var positionalIsModel = !string.IsNullOrWhiteSpace(modelValue)
                && _providers.Any(p => p.CanOpen(new ModelReference(modelValue)));

            ModelReference reference;
            string? pathFilter;

            if (positionalIsModel)
            {
                reference = new ModelReference(modelValue!);
                pathFilter = parseResult.GetValue(pathArgument);
            }
            else if (hasContextModel)
            {
                reference = activeReference;
                pathFilter = parseResult.GetValue(pathArgument) ?? modelValue;
            }
            else
            {
                reference = new ModelReference(modelValue ?? "");
                pathFilter = parseResult.GetValue(pathArgument);
            }
            var typeValue = parseResult.GetValue(typeOption);
            var pathsOnly = parseResult.GetValue(pathsOnlyOption);
            var noMultiline = parseResult.GetValue(noMultilineOption);
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);

            if (!CommandOutput.TryValidateFormat(formatValue))
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
                RenderCsv);
        });

        return command;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> ToReferenceJson(LsModelResult data)
        => data.Objects.Select(ToReferenceJson).ToList();

    private static IReadOnlyDictionary<string, object?> ToReferenceJson(LsObject obj)
    {
        if (obj.Kind == ModelObjectKind.Table)
        {
            return new Dictionary<string, object?>
            {
                ["name"] = obj.Name,
                ["description"] = obj.Description ?? "",
                ["isHidden"] = obj.Hidden,
                ["dataCategory"] = "",
                ["lineageTag"] = "",
                ["columns"] = obj.ChildCounts.GetValueOrDefault(ModelObjectKind.Column),
                ["measures"] = obj.ChildCounts.GetValueOrDefault(ModelObjectKind.Measure),
                ["hierarchies"] = obj.ChildCounts.GetValueOrDefault(ModelObjectKind.Hierarchy),
                ["partitions"] = obj.ChildCounts.GetValueOrDefault(ModelObjectKind.Partition),
                ["refreshPolicy"] = null,
                ["defaultDetailRowsExpression"] = null
            };
        }

        if (obj.Kind == ModelObjectKind.Column)
        {
            return new Dictionary<string, object?>
            {
                ["name"] = obj.Name,
                ["description"] = obj.Description ?? "",
                ["sourceColumn"] = obj.SourceColumn ?? "",
                ["dataType"] = DataTypeProperty(obj, DataTypeName(obj.Detail)),
                ["isHidden"] = obj.Hidden,
                ["formatString"] = Property(obj, "FormatString", ""),
                ["displayFolder"] = "",
                ["sortByColumn"] = EmptyToNull(Property(obj, "SortByColumn", "")),
                ["summarizeBy"] = Property(obj, "SummarizeBy", "Default"),
                ["lineageTag"] = ""
            };
        }

        if (obj.Kind == ModelObjectKind.Measure)
        {
            return new Dictionary<string, object?>
            {
                ["name"] = obj.Name,
                ["description"] = obj.Description ?? "",
                ["expression"] = obj.Expression ?? "",
                ["formatString"] = Property(obj, "FormatString", ""),
                ["isHidden"] = obj.Hidden,
                ["displayFolder"] = Property(obj, "DisplayFolder", ""),
                ["dataType"] = DataTypeProperty(obj, GuessMeasureDataType(obj.Expression)),
                ["detailRowsExpression"] = null,
                ["formatStringExpression"] = null,
                ["kpi"] = null,
                ["lineageTag"] = ""
            };
        }

        return new Dictionary<string, object?>
        {
            ["type"] = obj.Kind.ToString(),
            ["path"] = obj.Path,
            ["name"] = obj.Name,
            ["description"] = obj.Description ?? "",
            ["isHidden"] = obj.Hidden,
            ["detail"] = obj.Detail,
            ["expression"] = obj.Expression
        };
    }

    private static string Property(LsObject obj, string key, string fallback)
        => obj.Properties is not null && obj.Properties.TryGetValue(key, out var value)
            ? value
            : fallback;

    private static object? EmptyToNull(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string DataTypeProperty(LsObject obj, string fallback)
    {
        var value = Property(obj, "DataType", "");
        return string.IsNullOrWhiteSpace(value) || value.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            ? fallback
            : DataTypeName(value);
    }

    private static string DataTypeName(string? detail)
        => detail?.Trim().ToLowerInvariant() switch
        {
            "int64" => "Int64",
            "decimal" => "Decimal",
            "double" => "Double",
            "string" => "String",
            "boolean" or "bool" => "Boolean",
            "datetime" => "DateTime",
            _ => detail ?? ""
        };

    private static string GuessMeasureDataType(string? expression)
    {
        var text = expression ?? "";
        return text.Contains("COUNTROWS", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("DISTINCTCOUNT", StringComparison.OrdinalIgnoreCase)
            ? "Int64"
            : "Decimal";
    }

    private static void RenderCsv(LsModelResult data)
    {
        var objects = data.Objects;
        if (objects.Count > 0 && objects.All(o => o.Kind == ModelObjectKind.Table))
        {
            CsvOutput.Write(
                ["Name", "Columns", "Measures", "Partitions", "Hidden", "Description"],
                objects.Select(o => (IReadOnlyList<object?>)
                [
                    o.Name,
                    o.ChildCounts.GetValueOrDefault(ModelObjectKind.Column),
                    o.ChildCounts.GetValueOrDefault(ModelObjectKind.Measure),
                    o.ChildCounts.GetValueOrDefault(ModelObjectKind.Partition),
                    o.Hidden,
                    o.Description ?? ""
                ]));
            return;
        }

        if (objects.Count > 0 && objects.All(o => o.Kind == ModelObjectKind.Column))
        {
            CsvOutput.Write(
                ["Name", "SourceColumn", "DataType", "Description", "Hidden"],
                objects.Select(o => (IReadOnlyList<object?>)
                [
                    o.Name,
                    o.SourceColumn ?? "",
                    DataTypeDisplay(Property(o, "DataType", o.Detail ?? "")),
                    o.Description ?? "",
                    o.Hidden
                ]));
            return;
        }

        if (objects.Count > 0 && objects.All(o => o.Kind == ModelObjectKind.Measure))
        {
            CsvOutput.Write(
                ["Name", "Description", "Hidden", "Expression", "FormatString"],
                objects.Select(o => (IReadOnlyList<object?>)
                [
                    o.Name,
                    o.Description ?? "",
                    o.Hidden,
                    o.Expression ?? "",
                    ""
                ]));
            return;
        }

        CsvOutput.Write(
            ["Name", "Description", "Hidden", "Detail", "Expression"],
            objects.Select(o => (IReadOnlyList<object?>)
            [
                o.Name,
                o.Description ?? "",
                o.Hidden,
                o.Detail ?? "",
                o.Expression ?? ""
            ]));
    }

    private static string DataTypeDisplay(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "int64" => "Integer / Whole Number (int64)",
            "decimal" => "Currency / Fixed Decimal Number (decimal)",
            "string" => "String / Text",
            "double" => "Decimal Number (double)",
            "boolean" or "bool" => "Boolean / True/False",
            "datetime" => "DateTime / Date/Time",
            _ => value
        };
}
