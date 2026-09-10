using System.Diagnostics;
using Tomix.App.Dax;
using Tomix.App.ModelObjects;
using Tomix.App.Models;
using Tomix.App.Mutations;
using Tomix.Core.Dax;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Validate;

public sealed class ValidateModelHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public ValidateModelHandler(IEnumerable<IModelProvider> providers)
        => _providers = providers.ToList();

    public async Task<TomixResult<ValidateModelResult>> HandleAsync(
        ValidateModelRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await ModelSessionRunner.RunAsync(_providers, request.Model, async session =>
            {
                var snapshot = await session.GetSnapshotAsync(cancellationToken);

                var issues = request.ServerOnly
                    ? new LocalIssues([], [], null)
                    : ValidateLocal(snapshot);
                return Complete(request, stopwatch, issues, snapshot.Name);
            }, noProviderMessage: null, noProviderHint: null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A local model that cannot even be loaded (e.g. TMDL with unresolvable references)
            // is a validation failure, not a crash. Auth and remote-connect failures in the guard
            // stay diagnostics: they describe the connection, not the model.
            var issues = new LocalIssues(
                [new ValidationIssue("TOMIX_MODEL_LOAD_FAILED", ex.Message, request.Model.Value, Expression: null)],
                [],
                MeasureNames: null);
            return Complete(request, stopwatch, issues, request.Model.Value);
        }
    }

    private static TomixResult<ValidateModelResult> Complete(
        ValidateModelRequest request,
        Stopwatch stopwatch,
        LocalIssues issues,
        string modelName)
    {
        stopwatch.Stop();

        var result = new ValidateModelResult(
            ModelName: modelName,
            Valid: issues.Errors.Count == 0,
            DurationMs: Math.Max(0, stopwatch.ElapsedMilliseconds),
            Errors: issues.Errors,
            Warnings: request.NoWarnings ? [] : issues.Warnings,
            MeasureNames: issues.MeasureNames);

        return TomixResult<ValidateModelResult>.Ok(result, exitCode: result.Valid ? 0 : 1);
    }

    private sealed record LocalIssues(
        IReadOnlyList<ValidationIssue> Errors,
        IReadOnlyList<ValidationIssue> Warnings,
        IReadOnlySet<string>? MeasureNames);

    /// <summary>
    /// Offline analysis over the snapshot: every DAX-bearing property (via
    /// <see cref="DaxExpressions"/>) is scanned with <see cref="DaxReferenceExtractor"/> so
    /// references inside string literals and comments are never reported, plus structural
    /// integrity checks (relationship endpoints, sort-by columns, hierarchy levels) that DAX
    /// scanning cannot see.
    /// </summary>
    private static LocalIssues ValidateLocal(ModelSnapshot snapshot)
    {
        var objects = ModelObjectProjection.Flatten(snapshot);
        var index = ModelNameIndex.Build(objects);

        var errors = new List<ValidationIssue>();
        var warnings = new List<ValidationIssue>();

        foreach (var obj in objects)
        {
            foreach (var site in DaxExpressions.Sites(obj))
                CheckDaxSite(obj, site, index, errors, warnings);

            CheckStructure(obj, index, errors);
        }

        return new LocalIssues(Distinct(errors), Distinct(warnings), index.MeasureNames);
    }

    private static void CheckDaxSite(
        ModelObject obj,
        DaxSite site,
        ModelNameIndex index,
        List<ValidationIssue> errors,
        List<ValidationIssue> warnings)
    {
        // Broken syntax cascades: a never-closed bracket makes every reference after it read
        // wrong, so report the syntax and skip the reference checks entirely.
        var syntax = DaxSyntaxCheck.Analyze(site.Expression);
        if (syntax.Count > 0)
        {
            foreach (var issue in syntax)
                errors.Add(new ValidationIssue(
                    SyntaxCode(issue.Kind),
                    issue.Message,
                    obj.Path,
                    Line(site.Expression, issue.Start),
                    LineText(site.Expression, issue.Start)));
            return;
        }

        foreach (var reference in DaxReferenceExtractor.Extract(site.Expression))
        {
            switch (reference.Shape)
            {
                case DaxReferenceShape.Qualified:
                    if (!index.TableColumns.TryGetValue(reference.Table!, out var columns))
                        errors.Add(new ValidationIssue(
                            "DAX0001",
                            $"Table '{reference.Table}' cannot be found.",
                            obj.Path,
                            Line(site.Expression, reference.Start),
                            LineText(site.Expression, reference.Start)));
                    else if (!columns.Contains(reference.Object!)
                        && !index.MeasureNames.Contains(reference.Object!))
                        errors.Add(new ValidationIssue(
                            "DAX0002",
                            $"Column [{reference.Object}] cannot be found on table '{reference.Table}'.",
                            obj.Path,
                            Line(site.Expression, reference.Start),
                            LineText(site.Expression, reference.Start)));
                    break;

                case DaxReferenceShape.Table:
                    if (!index.TableColumns.ContainsKey(reference.Table!))
                        errors.Add(new ValidationIssue(
                            "DAX0001",
                            $"Table '{reference.Table}' cannot be found.",
                            obj.Path,
                            Line(site.Expression, reference.Start),
                            LineText(site.Expression, reference.Start)));
                    break;

                // A lone [X] that resolves nowhere may still be a query-scoped extension column
                // (ADDCOLUMNS/SUMMARIZE), which the extractor cannot see — warn, don't fail.
                case DaxReferenceShape.Unqualified:
                    if (!index.MeasureNames.Contains(reference.Object!)
                        && !index.ColumnNames.Contains(reference.Object!))
                        warnings.Add(new ValidationIssue(
                            "DAX0003",
                            $"Measure or column [{reference.Object}] cannot be found in the model.",
                            obj.Path,
                            Line(site.Expression, reference.Start),
                            LineText(site.Expression, reference.Start)));
                    break;

                // A bare word only counts as a table when the model has one by that name.
                case DaxReferenceShape.TableCandidate:
                    break;
            }
        }
    }

    /// <summary>DAX0004 for illegal characters and unbalanced groups, DAX0005 for unterminated literals/comments.</summary>
    private static string SyntaxCode(DaxSyntaxErrorKind kind) =>
        kind is DaxSyntaxErrorKind.UnterminatedLiteral or DaxSyntaxErrorKind.UnterminatedComment
            ? "DAX0005"
            : "DAX0004";

    private static void CheckStructure(ModelObject obj, ModelNameIndex index, List<ValidationIssue> errors)
    {
        switch (obj.Kind)
        {
            case ModelObjectKind.Relationship:
                CheckRelationshipEndpoint(obj, obj.Property("FromColumn"), index, errors);
                CheckRelationshipEndpoint(obj, obj.Property("ToColumn"), index, errors);
                break;

            case ModelObjectKind.Column:
                var sortBy = obj.Property("SortByColumn");
                if (!string.IsNullOrWhiteSpace(sortBy)
                    && index.TableColumns.TryGetValue(OwningTable(obj.Path), out var siblings)
                    && !siblings.Contains(sortBy!))
                    errors.Add(new ValidationIssue(
                        "TOMIX_BROKEN_SORT_BY",
                        $"Sort-by column '{sortBy}' cannot be found on table '{OwningTable(obj.Path)}'.",
                        obj.Path,
                        Expression: null));
                break;

            // Detail carries the level's bound column name; empty means the provider had none.
            case ModelObjectKind.Level:
                if (!string.IsNullOrWhiteSpace(obj.Detail)
                    && index.TableColumns.TryGetValue(OwningTable(obj.Path), out var tableColumns)
                    && !tableColumns.Contains(obj.Detail!))
                    errors.Add(new ValidationIssue(
                        "TOMIX_BROKEN_LEVEL",
                        $"Hierarchy level '{obj.Name}' is bound to column '{obj.Detail}', which cannot be found on table '{OwningTable(obj.Path)}'.",
                        obj.Path,
                        Expression: null));
                break;
        }
    }

    private static void CheckRelationshipEndpoint(
        ModelObject relationship,
        string? endpoint,
        ModelNameIndex index,
        List<ValidationIssue> errors)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || !DaxObjectForm.TryParse(endpoint!, out var table, out var column))
            return;

        if (index.TableColumns.TryGetValue(table, out var columns) && columns.Contains(column))
            return;

        errors.Add(new ValidationIssue(
            "TOMIX_BROKEN_RELATIONSHIP",
            $"Relationship endpoint '{table}'[{column}] refers to a missing column.",
            relationship.Path,
            Expression: null));
    }

    private static List<ValidationIssue> Distinct(List<ValidationIssue> issues)
        => issues.DistinctBy(issue => (issue.Code, issue.Message, issue.ObjectName)).ToList();

    /// <summary>Very long offending lines are truncated so one expression cannot dominate the table.</summary>
    private const int MaxExpressionLine = 120;

    /// <summary>The 1-based line of <paramref name="offset"/> in <paramref name="expression"/>.</summary>
    private static string Line(string expression, int offset)
        => (LineIndex(expression, offset) + 1).ToString();

    /// <summary>
    /// The offending line's text for human output: trailing whitespace trimmed and very long
    /// lines truncated with an ellipsis (the <c>ls</c> preview precedent). The line is found
    /// with the same newline accounting as <see cref="Line"/>, so the number and the text agree.
    /// </summary>
    private static string? LineText(string expression, int offset)
    {
        if (expression.Length == 0)
            return null;

        var lines = expression.Split('\n');
        var line = Math.Min(LineIndex(expression, offset), lines.Length - 1);
        var text = lines[line].TrimEnd();
        return text.Length > MaxExpressionLine ? text[..(MaxExpressionLine - 3)] + "..." : text;
    }

    /// <summary>The 0-based index of the line containing <paramref name="offset"/>.</summary>
    private static int LineIndex(string expression, int offset)
    {
        var line = 0;
        for (var i = 0; i < offset && i < expression.Length; i++)
        {
            if (expression[i] == '\n')
                line++;
        }

        return line;
    }

    private static string OwningTable(string path)
    {
        var slash = path.IndexOf('/');
        return slash < 0 ? path : path[..slash];
    }

    /// <summary>Name lookups shared by the DAX and structural checks.</summary>
    private sealed record ModelNameIndex(
        Dictionary<string, HashSet<string>> TableColumns,
        HashSet<string> MeasureNames,
        HashSet<string> ColumnNames)
    {
        public static ModelNameIndex Build(IReadOnlyList<ModelObject> objects)
        {
            var tableColumns = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var measureNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var columnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var table in objects.Where(o => o.Kind == ModelObjectKind.Table))
            {
                var columns = table.Children
                    .Where(c => c.Kind == ModelObjectKind.Column)
                    .Select(c => c.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                tableColumns.TryAdd(table.Name, columns);
                columnNames.UnionWith(columns);

                foreach (var measure in table.Children.Where(c => c.Kind == ModelObjectKind.Measure))
                    measureNames.Add(measure.Name);
            }

            return new ModelNameIndex(tableColumns, measureNames, columnNames);
        }
    }
}
