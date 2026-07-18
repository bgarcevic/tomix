using System.Text.RegularExpressions;
using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using static Tomix.Provider.Tom.TomMutationPaths;

namespace Tomix.Provider.Tom;

/// <summary>
/// Model-wide text find/replace across names, expressions, descriptions, display folders,
/// format strings, and (explicit-only) annotations, with per-property previews.
/// </summary>
internal sealed class TomTextReplacer
{
    private readonly Database _database;

    public TomTextReplacer(Database database) => _database = database;

    public ModelReplaceResult Replace(ModelReplaceRequest request)
    {
        var operations = BuildReplaceOperations(request)
            .Where(o => !string.Equals(o.Preview.Before, o.Preview.After, StringComparison.Ordinal))
            .ToList();
        if (request.Apply)
        {
            foreach (var operation in operations)
                operation.Apply(operation.Preview.After);
        }

        return new ModelReplaceResult(
            operations.Count,
            operations.Select(o => o.Preview).ToList());
    }

    private IEnumerable<ReplaceOperation> BuildReplaceOperations(ModelReplaceRequest request)
    {
        var scope = NormalizeReplaceScope(request.Scope);

        if (scope is "all" or "names")
        {
            foreach (var operation in BuildNameReplaceOperations(request))
                yield return operation;
        }

        if (scope is "all" or "expressions")
        {
            foreach (var operation in BuildExpressionReplaceOperations(request))
                yield return operation;
        }

        if (scope is "all" or "descriptions")
        {
            foreach (var operation in BuildDescriptionReplaceOperations(request))
                yield return operation;
        }

        if (scope is "all" or "displayfolders")
        {
            foreach (var operation in BuildDisplayFolderReplaceOperations(request))
                yield return operation;
        }

        if (scope is "all" or "formatstrings")
        {
            foreach (var operation in BuildFormatStringReplaceOperations(request))
                yield return operation;
        }

        // Annotations are explicit-only: values are often tool-generated JSON, so a blanket
        // '--in all' replace must not rewrite them.
        if (scope is "annotations")
        {
            foreach (var operation in BuildAnnotationReplaceOperations(request))
                yield return operation;
        }
    }

    private IEnumerable<ReplaceOperation> BuildAnnotationReplaceOperations(ModelReplaceRequest request)
    {
        foreach (var (path, annotations) in EnumerateAnnotationOwners())
        {
            foreach (var annotation in annotations)
            {
                var value = annotation;
                yield return ReplaceProperty(
                    path, $"Annotation:{annotation.Name}", annotation.Value, v => value.Value = v, request);
            }
        }
    }

    private IEnumerable<(string Path, IEnumerable<Annotation> Annotations)> EnumerateAnnotationOwners()
    {
        yield return (".", _database.Model.Annotations);

        foreach (var table in _database.Model.Tables)
        {
            var tablePath = Segment(table.Name);
            yield return (tablePath, table.Annotations);

            foreach (var measure in table.Measures)
                yield return ($"{tablePath}/{Segment(measure.Name)}", measure.Annotations);

            foreach (var column in table.Columns.Where(c => c.Type != ColumnType.RowNumber))
                yield return ($"{tablePath}/{Segment(column.Name)}", column.Annotations);

            foreach (var hierarchy in table.Hierarchies)
                yield return ($"{tablePath}/{Segment(hierarchy.Name)}", hierarchy.Annotations);

            foreach (var partition in table.Partitions)
                yield return ($"{tablePath}/Partitions/{Segment(partition.Name)}", partition.Annotations);
        }

        foreach (var role in _database.Model.Roles)
            yield return ($"Roles/{Segment(role.Name)}", role.Annotations);
    }

    private IEnumerable<ReplaceOperation> BuildNameReplaceOperations(ModelReplaceRequest request)
    {
        foreach (var table in _database.Model.Tables)
        {
            var tablePath = Segment(table.Name);

            yield return ReplaceProperty(tablePath, "Name", table.Name, value => table.Name = value, request);

            foreach (var measure in table.Measures)
            {
                var measurePath = $"{tablePath}/{Segment(measure.Name)}";
                yield return ReplaceProperty(measurePath, "Name", measure.Name, value => measure.Name = value, request);
            }

            foreach (var column in table.Columns.Where(c => c.Type != ColumnType.RowNumber))
            {
                var columnPath = $"{tablePath}/{Segment(column.Name)}";
                yield return ReplaceProperty(columnPath, "Name", column.Name, value => column.Name = value, request);
            }

            foreach (var hierarchy in table.Hierarchies)
            {
                var hierarchyPath = $"{tablePath}/{Segment(hierarchy.Name)}";
                yield return ReplaceProperty(hierarchyPath, "Name", hierarchy.Name, value => hierarchy.Name = value, request);
            }
        }

        foreach (var role in _database.Model.Roles)
        {
            var rolePath = $"Roles/{Segment(role.Name)}";
            yield return ReplaceProperty(rolePath, "Name", role.Name, value => role.Name = value, request);
        }
    }

    private IEnumerable<ReplaceOperation> BuildExpressionReplaceOperations(ModelReplaceRequest request)
    {
        foreach (var table in _database.Model.Tables)
        {
            var tablePath = Segment(table.Name);
            foreach (var measure in table.Measures)
            {
                var measurePath = $"{tablePath}/{Segment(measure.Name)}";
                yield return ReplaceProperty(measurePath, "Expression", measure.Expression, value => measure.Expression = value, request);
            }

            foreach (var column in table.Columns.OfType<CalculatedColumn>())
            {
                var columnPath = $"{tablePath}/{Segment(column.Name)}";
                yield return ReplaceProperty(columnPath, "Expression", column.Expression, value => column.Expression = value, request);
            }

            foreach (var partition in table.Partitions)
            {
                var partitionPath = $"{tablePath}/Partitions/{Segment(partition.Name)}";
                if (partition.Source is MPartitionSource source)
                    yield return ReplaceProperty(partitionPath, "Expression", source.Expression, value => source.Expression = value, request);
            }
        }
    }

    private IEnumerable<ReplaceOperation> BuildDescriptionReplaceOperations(ModelReplaceRequest request)
    {
        foreach (var table in _database.Model.Tables)
        {
            var tablePath = Segment(table.Name);
            yield return ReplaceProperty(tablePath, "Description", table.Description, value => table.Description = value, request);

            foreach (var measure in table.Measures)
            {
                var measurePath = $"{tablePath}/{Segment(measure.Name)}";
                yield return ReplaceProperty(measurePath, "Description", measure.Description, value => measure.Description = value, request);
            }

            foreach (var column in table.Columns.Where(c => c.Type != ColumnType.RowNumber))
            {
                var columnPath = $"{tablePath}/{Segment(column.Name)}";
                yield return ReplaceProperty(columnPath, "Description", column.Description, value => column.Description = value, request);
            }

            foreach (var hierarchy in table.Hierarchies)
            {
                var hierarchyPath = $"{tablePath}/{Segment(hierarchy.Name)}";
                yield return ReplaceProperty(hierarchyPath, "Description", hierarchy.Description, value => hierarchy.Description = value, request);
            }
        }

        foreach (var role in _database.Model.Roles)
        {
            var rolePath = $"Roles/{Segment(role.Name)}";
            yield return ReplaceProperty(rolePath, "Description", role.Description, value => role.Description = value, request);
        }
    }

    private IEnumerable<ReplaceOperation> BuildDisplayFolderReplaceOperations(ModelReplaceRequest request)
    {
        foreach (var table in _database.Model.Tables)
        {
            var tablePath = Segment(table.Name);
            foreach (var measure in table.Measures)
            {
                var measurePath = $"{tablePath}/{Segment(measure.Name)}";
                yield return ReplaceProperty(measurePath, "DisplayFolder", measure.DisplayFolder, value => measure.DisplayFolder = value, request);
            }

            foreach (var column in table.Columns.Where(c => c.Type != ColumnType.RowNumber))
            {
                var columnPath = $"{tablePath}/{Segment(column.Name)}";
                yield return ReplaceProperty(columnPath, "DisplayFolder", column.DisplayFolder, value => column.DisplayFolder = value, request);
            }

            foreach (var hierarchy in table.Hierarchies)
            {
                var hierarchyPath = $"{tablePath}/{Segment(hierarchy.Name)}";
                yield return ReplaceProperty(hierarchyPath, "DisplayFolder", hierarchy.DisplayFolder, value => hierarchy.DisplayFolder = value, request);
            }
        }
    }

    private IEnumerable<ReplaceOperation> BuildFormatStringReplaceOperations(ModelReplaceRequest request)
    {
        foreach (var table in _database.Model.Tables)
        {
            var tablePath = Segment(table.Name);
            foreach (var measure in table.Measures)
            {
                var measurePath = $"{tablePath}/{Segment(measure.Name)}";
                yield return ReplaceProperty(measurePath, "FormatString", measure.FormatString, value => measure.FormatString = value, request);
            }

            foreach (var column in table.Columns.Where(c => c.Type != ColumnType.RowNumber))
            {
                var columnPath = $"{tablePath}/{Segment(column.Name)}";
                yield return ReplaceProperty(columnPath, "FormatString", column.FormatString, value => column.FormatString = value, request);
            }
        }
    }

    private static ReplaceOperation ReplaceProperty(
        string objectPath,
        string property,
        string? value,
        Action<string> apply,
        ModelReplaceRequest request)
    {
        var before = value ?? "";
        var after = ReplaceValue(before, request);
        return new ReplaceOperation(
            new ModelReplacePreview(objectPath, property, before, after),
            apply);
    }

    private static string ReplaceValue(string value, ModelReplaceRequest request)
    {
        if (string.IsNullOrEmpty(request.Pattern))
            return value;

        if (request.Regex)
        {
            var options = request.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            return Regex.Replace(value, request.Pattern, request.Replacement, options);
        }

        var comparison = request.CaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        var index = value.IndexOf(request.Pattern, comparison);
        if (index < 0)
            return value;

        var result = new System.Text.StringBuilder();
        var start = 0;
        while (index >= 0)
        {
            result.Append(value, start, index - start);
            result.Append(request.Replacement);
            start = index + request.Pattern.Length;
            index = value.IndexOf(request.Pattern, start, comparison);
        }

        result.Append(value, start, value.Length - start);
        return result.ToString();
    }

    private static string NormalizeReplaceScope(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return "all";

        return scope.Trim().ToLowerInvariant() switch
        {
            "name" or "names" => "names",
            "expression" or "expressions" => "expressions",
            "description" or "descriptions" => "descriptions",
            "displayfolder" or "displayfolders" or "display-folders" => "displayfolders",
            "formatstring" or "formatstrings" or "format-strings" => "formatstrings",
            "annotation" or "annotations" => "annotations",
            "all" => "all",
            // An unknown scope would match no Build*ReplaceOperations branch and silently
            // replace nothing; reject it instead.
            _ => throw new ArgumentException(
                $"Unknown replace scope: '{scope}'. Known values: names, expressions, descriptions, displayFolders, formatStrings, annotations, all.")
        };
    }

    private sealed record ReplaceOperation(
        ModelReplacePreview Preview,
        Action<string> Apply);
}
