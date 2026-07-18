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

    /// <summary>
    /// Single model walk; each object yields the property sites belonging to the requested
    /// scope. Under scope "all" the previews therefore group by object, not by property kind.
    /// </summary>
    private IEnumerable<ReplaceOperation> BuildReplaceOperations(ModelReplaceRequest request)
    {
        var scope = NormalizeReplaceScope(request.Scope);

        // Annotations are explicit-only: values are often tool-generated JSON, so a blanket
        // '--in all' replace must not rewrite them.
        bool In(string candidate) => scope == candidate || (scope == "all" && candidate != "annotations");

        ReplaceOperation Op(string path, string property, string? value, Action<string> apply)
            => ReplaceProperty(path, property, value, apply, request);

        IEnumerable<ReplaceOperation> AnnotationOps(string path, IEnumerable<Annotation> annotations)
        {
            foreach (var annotation in annotations)
            {
                var target = annotation;
                yield return Op(path, $"Annotation:{annotation.Name}", target.Value, v => target.Value = v);
            }
        }

        if (In("annotations"))
        {
            foreach (var op in AnnotationOps(".", _database.Model.Annotations))
                yield return op;
        }

        foreach (var table in _database.Model.Tables)
        {
            var tablePath = Segment(table.Name);
            if (In("names"))
                yield return Op(tablePath, "Name", table.Name, v => table.Name = v);
            if (In("descriptions"))
                yield return Op(tablePath, "Description", table.Description, v => table.Description = v);
            if (In("annotations"))
            {
                foreach (var op in AnnotationOps(tablePath, table.Annotations))
                    yield return op;
            }

            foreach (var measure in table.Measures)
            {
                var path = $"{tablePath}/{Segment(measure.Name)}";
                if (In("names"))
                    yield return Op(path, "Name", measure.Name, v => measure.Name = v);
                if (In("expressions"))
                    yield return Op(path, "Expression", measure.Expression, v => measure.Expression = v);
                if (In("descriptions"))
                    yield return Op(path, "Description", measure.Description, v => measure.Description = v);
                if (In("displayfolders"))
                    yield return Op(path, "DisplayFolder", measure.DisplayFolder, v => measure.DisplayFolder = v);
                if (In("formatstrings"))
                    yield return Op(path, "FormatString", measure.FormatString, v => measure.FormatString = v);
                if (In("annotations"))
                {
                    foreach (var op in AnnotationOps(path, measure.Annotations))
                        yield return op;
                }
            }

            foreach (var column in table.Columns.Where(c => c.Type != ColumnType.RowNumber))
            {
                var path = $"{tablePath}/{Segment(column.Name)}";
                if (In("names"))
                    yield return Op(path, "Name", column.Name, v => column.Name = v);
                if (In("expressions") && column is CalculatedColumn calculated)
                    yield return Op(path, "Expression", calculated.Expression, v => calculated.Expression = v);
                if (In("descriptions"))
                    yield return Op(path, "Description", column.Description, v => column.Description = v);
                if (In("displayfolders"))
                    yield return Op(path, "DisplayFolder", column.DisplayFolder, v => column.DisplayFolder = v);
                if (In("formatstrings"))
                    yield return Op(path, "FormatString", column.FormatString, v => column.FormatString = v);
                if (In("annotations"))
                {
                    foreach (var op in AnnotationOps(path, column.Annotations))
                        yield return op;
                }
            }

            foreach (var hierarchy in table.Hierarchies)
            {
                var path = $"{tablePath}/{Segment(hierarchy.Name)}";
                if (In("names"))
                    yield return Op(path, "Name", hierarchy.Name, v => hierarchy.Name = v);
                if (In("descriptions"))
                    yield return Op(path, "Description", hierarchy.Description, v => hierarchy.Description = v);
                if (In("displayfolders"))
                    yield return Op(path, "DisplayFolder", hierarchy.DisplayFolder, v => hierarchy.DisplayFolder = v);
                if (In("annotations"))
                {
                    foreach (var op in AnnotationOps(path, hierarchy.Annotations))
                        yield return op;
                }
            }

            foreach (var partition in table.Partitions)
            {
                var path = $"{tablePath}/Partitions/{Segment(partition.Name)}";
                if (In("expressions") && partition.Source is MPartitionSource source)
                    yield return Op(path, "Expression", source.Expression, v => source.Expression = v);
                if (In("annotations"))
                {
                    foreach (var op in AnnotationOps(path, partition.Annotations))
                        yield return op;
                }
            }
        }

        foreach (var role in _database.Model.Roles)
        {
            var path = $"Roles/{Segment(role.Name)}";
            if (In("names"))
                yield return Op(path, "Name", role.Name, v => role.Name = v);
            if (In("descriptions"))
                yield return Op(path, "Description", role.Description, v => role.Description = v);
            if (In("annotations"))
            {
                foreach (var op in AnnotationOps(path, role.Annotations))
                    yield return op;
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
