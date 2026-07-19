using System.Globalization;
using Tomix.App.Get;
using Tomix.Core.Models;
using Tomix.Core.Properties;

namespace Tomix.Cli.Output;

internal static class GetRenderer
{
    public static void Render(GetModelResult result, string format)
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

    public static void RenderCsv(GetModelResult result)
    {
        if (result.Properties.Count == 1)
        {
            CsvOutput.WriteValue(result.Properties.Values.First());
            return;
        }

        PropertyCsvRenderer.Write(ModelPropertyCatalog.For(result.Object.Kind), result.Properties);
    }

    public static object? ToReferenceJson(GetModelResult result)
        => IsScalarQuery(result) ? result.Properties.Values.First() : result;

    private static bool IsScalarQuery(GetModelResult result)
        => result.Properties.Count == 1;

    private static void RenderProperties(GetModelResult result)
    {
        Console.WriteLine($"{result.Path} ({result.Type})");
        foreach (var (key, value) in result.Properties)
            Console.WriteLine($"{key}: {value}");
    }

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
