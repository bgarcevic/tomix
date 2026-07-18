using System.CommandLine;
using System.Globalization;
using Tomix.App.Get;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Models;
using Tomix.Core.Properties;
using Spectre.Console;

namespace Tomix.Cli.Commands;

internal sealed class GetCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public GetCommand(IReadOnlyList<IModelProvider> providers) => _providers = providers;

    public Command Build()
    {
        var pathArgument = new Argument<string>("path")
        {
            Description = "Object path. Slash-separated: 'Sales', 'Sales/Amount'. DAX forms also accepted."
        };

        var modelArgument = new Argument<string>("model")
        {
            Description = "Path to model (if not using --model)",
            Arity = ArgumentArity.ZeroOrOne
        };

        var queryOption = new Option<string?>("--query")
        {
            Description = "Query a specific property (e.g., -q expression, -q formatString)"
        };
        queryOption.Aliases.Add("-q");

        var typeOption = new Option<string?>("--type")
        {
            Description = "Disambiguate when the path matches multiple table-children."
        };
        typeOption.Aliases.Add("-t");

        var command = new Command("get", "Get properties of a model object")
        {
            pathArgument,
            modelArgument,
            queryOption,
            typeOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var path = parseResult.GetValue(pathArgument) ?? "";
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            var errorFormat = parseResult.GetValue(GlobalOptions.ErrorFormat);
            var query = parseResult.GetValue(queryOption);
            var typeValue = parseResult.GetValue(typeOption);

            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "get", OutputFormats.Text, OutputFormats.Json, OutputFormats.Csv, OutputFormats.Tmdl, OutputFormats.Bim, OutputFormats.Tmsl))
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
            var reference = RecentConnections.CreateResolver(source).ResolveReference(source.Model, source.Database, source.Server);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var result = await CliSpinner.RunAsync(
                "Loading model...",
                () => new GetModelHandler(_providers).HandleAsync(
                    new GetModelRequest(
                        reference,
                        path, query, type),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(formatValue) || OutputFormats.IsCsv(formatValue));

            return CommandOutput.Render(
                result,
                formatValue,
                data => Render(data, formatValue),
                ToReferenceJson,
                renderCsv: RenderCsv,
                errorFormat: errorFormat);
        });

        return command;
    }

    private static void Render(GetModelResult result, string format)
    {
        if (IsScalarQuery(result))
        {
            RenderScalar(result.Properties.Values.First());
            return;
        }

        if (format is OutputFormats.Tmdl)
        {
            RenderTmdl(result);
            return;
        }

        if (format is OutputFormats.Bim or OutputFormats.Tmsl)
        {
            RenderBim(result);
            return;
        }

        RenderProperties(result);
    }

    private static void RenderProperties(GetModelResult result)
    {
        Console.WriteLine($"{result.Path} ({result.Type})");
        foreach (var (key, value) in result.Properties)
            Console.WriteLine($"{key}: {value}");
    }

    private static void RenderCsv(GetModelResult result)
    {
        if (result.Properties.Count == 1)
        {
            CsvOutput.WriteValue(result.Properties.Values.First());
            return;
        }

        PropertyCsvRenderer.Write(ModelPropertyCatalog.For(result.Object.Kind), result.Properties);
    }

    private static object? ToReferenceJson(GetModelResult result)
        => IsScalarQuery(result) ? result.Properties.Values.First() : result;

    private static bool IsScalarQuery(GetModelResult result)
        => result.Properties.Count == 1;

    private static void RenderScalar(object? value)
    {
        Console.WriteLine(value switch
        {
            null => "",
            bool b => b ? "True" : "False",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        });
    }

    private static void RenderTmdl(GetModelResult result)
    {
        switch (result.Object.Kind)
        {
            case ModelObjectKind.Table:
                RenderTableTmdl(result.Object);
                return;
            case ModelObjectKind.Measure:
                RenderChildTmdl(result.Object, RenderMeasureTmdl);
                return;
            case ModelObjectKind.Column:
                RenderChildTmdl(result.Object, RenderColumnTmdl);
                return;
            case ModelObjectKind.Partition:
                RenderChildTmdl(result.Object, RenderPartitionTmdl);
                return;
            default:
                RenderProperties(result);
                return;
        }
    }

    private static void RenderTableTmdl(ModelObject table)
    {
        Console.WriteLine($"table {TmdlIdentifier(table.Name)}");
        Console.WriteLine();

        foreach (var measure in table.Children.Where(child => child.Kind == ModelObjectKind.Measure))
            RenderMeasureTmdl(measure);

        foreach (var column in table.Children.Where(child => child.Kind == ModelObjectKind.Column))
            RenderColumnTmdl(column);

        foreach (var partition in table.Children.Where(child => child.Kind == ModelObjectKind.Partition))
            RenderPartitionTmdl(partition);
    }

    private static void RenderChildTmdl(ModelObject obj, Action<ModelObject> renderObject)
    {
        Console.WriteLine($"ref table {TmdlIdentifier(ParentTableName(obj.Path))}");
        Console.WriteLine();
        renderObject(obj);
    }

    private static void RenderMeasureTmdl(ModelObject measure)
    {
        Console.WriteLine($"\tmeasure {TmdlIdentifier(measure.Name)} = {measure.Expression ?? ""}");
        Console.WriteLine();
    }

    private static void RenderColumnTmdl(ModelObject column)
    {
        Console.WriteLine($"\tcolumn {TmdlIdentifier(column.Name)}");
        if (!string.IsNullOrWhiteSpace(column.Detail))
            Console.WriteLine($"\t\tdataType: {column.Detail}");
        if (!string.IsNullOrWhiteSpace(column.SourceColumn))
            Console.WriteLine($"\t\tsourceColumn: {column.SourceColumn}");
        Console.WriteLine();
    }

    private static void RenderPartitionTmdl(ModelObject partition)
    {
        Console.WriteLine($"\tpartition {TmdlIdentifier(partition.Name)} = m");
        if (!string.IsNullOrWhiteSpace(partition.Detail))
            Console.WriteLine($"\t\tmode: {partition.Detail}");
        if (!string.IsNullOrWhiteSpace(partition.Expression))
            Console.WriteLine($"\t\tsource = {partition.Expression}");
        Console.WriteLine();
    }

    private static void RenderBim(GetModelResult result)
    {
        switch (result.Object.Kind)
        {
            case ModelObjectKind.Table:
                RenderTableBim(result.Object);
                return;
            case ModelObjectKind.Measure:
                RenderMeasureBim(result.Object);
                return;
            case ModelObjectKind.Column:
                RenderColumnBim(result.Object);
                return;
            case ModelObjectKind.Partition:
                RenderPartitionBim(result.Object);
                return;
            default:
                RenderProperties(result);
                return;
        }
    }

    private static void RenderTableBim(ModelObject table)
    {
        JsonOutput.Write(new
        {
            name = table.Name,
            columns = table.Children
                .Where(child => child.Kind == ModelObjectKind.Column)
                .Select(column => new
                {
                    name = column.Name,
                    dataType = column.Detail ?? "",
                    sourceColumn = column.SourceColumn ?? ""
                }),
            partitions = table.Children
                .Where(child => child.Kind == ModelObjectKind.Partition)
                .Select(partition => new
                {
                    name = partition.Name,
                    mode = partition.Detail ?? "",
                    source = new
                    {
                        type = "m",
                        expression = partition.Expression ?? ""
                    }
                }),
            measures = table.Children
                .Where(child => child.Kind == ModelObjectKind.Measure)
                .Select(measure => new
                {
                    name = measure.Name,
                    expression = measure.Expression ?? ""
                })
        });
    }

    private static void RenderMeasureBim(ModelObject measure)
    {
        JsonOutput.Write(new
        {
            name = measure.Name,
            expression = measure.Expression ?? ""
        });
    }

    private static void RenderColumnBim(ModelObject column)
    {
        JsonOutput.Write(new
        {
            name = column.Name,
            dataType = column.Detail ?? "",
            sourceColumn = column.SourceColumn ?? ""
        });
    }

    private static void RenderPartitionBim(ModelObject partition)
    {
        JsonOutput.Write(new
        {
            name = partition.Name,
            mode = partition.Detail ?? "",
            source = new
            {
                type = "m",
                expression = partition.Expression ?? ""
            }
        });
    }

    private static string ParentTableName(string path)
    {
        var slash = path.IndexOf('/');
        return slash < 0 ? path : path[..slash].Trim('\'');
    }

    private static string TmdlIdentifier(string name)
    {
        if (name.Length > 0 &&
            (char.IsLetter(name[0]) || name[0] == '_') &&
            name.All(c => char.IsLetterOrDigit(c) || c == '_'))
            return name;

        return $"'{name.Replace("'", "''", StringComparison.Ordinal)}'";
    }
}
