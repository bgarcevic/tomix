using System.Diagnostics;
using System.Text.RegularExpressions;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Validate;

public sealed partial class ValidateModelHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public ValidateModelHandler(IEnumerable<IModelProvider> providers)
        => _providers = providers.ToList();

    public async Task<TomixResult<ValidateModelResult>> HandleAsync(
        ValidateModelRequest request,
        CancellationToken cancellationToken)
    {
        var provider = _providers.FirstOrDefault(p => p.CanOpen(request.Model));
        if (provider is null)
            return TomixResult<ValidateModelResult>.Fail(
                "TOMIX_NO_PROVIDER",
                $"No provider can open model: {request.Model.Value}",
                exitCode: 2);

        var stopwatch = Stopwatch.StartNew();
        await using var session = await provider.OpenAsync(request.Model, cancellationToken);
        var snapshot = await session.GetSnapshotAsync(cancellationToken);

        var errors = request.ServerOnly
            ? []
            : ValidateLocalReferences(snapshot);
        stopwatch.Stop();

        var result = new ValidateModelResult(
            Valid: errors.Count == 0,
            DurationMs: Math.Max(0, stopwatch.ElapsedMilliseconds),
            Errors: errors,
            Warnings: []);

        return TomixResult<ValidateModelResult>.Ok(result, exitCode: result.Valid ? 0 : 1);
    }

    private static IReadOnlyList<ValidationIssue> ValidateLocalReferences(ModelSnapshot snapshot)
    {
        var tableColumns = snapshot.Objects
            .Where(o => o.Kind == ModelObjectKind.Table)
            .ToDictionary(
                table => table.Name,
                table => table.Children
                    .Where(c => c.Kind == ModelObjectKind.Column)
                    .Select(c => c.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        var measureNames = snapshot.Objects
            .Where(o => o.Kind == ModelObjectKind.Table)
            .SelectMany(t => t.Children)
            .Where(c => c.Kind == ModelObjectKind.Measure)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var issues = new List<ValidationIssue>();
        foreach (var obj in Flatten(snapshot.Objects))
        {
            if (string.IsNullOrWhiteSpace(obj.Expression))
                continue;

            foreach (var reference in ExtractColumnReferences(obj.Expression))
            {
                if (!tableColumns.TryGetValue(reference.Table, out var columns))
                    continue;

                if (columns.Contains(reference.Column))
                    continue;

                if (measureNames.Contains(reference.Column))
                    continue;

                issues.Add(new ValidationIssue(
                    "DAX0002",
                    $"Column [{reference.Column}] cannot be found on table '{reference.Table}'.",
                    obj.Name,
                    "1"));
            }
        }

        return issues
            .DistinctBy(issue => (issue.Code, issue.Message, issue.ObjectName))
            .ToList();
    }

    private static IEnumerable<ModelObject> Flatten(IEnumerable<ModelObject> objects)
    {
        foreach (var obj in objects)
        {
            yield return obj;
            foreach (var child in Flatten(obj.Children))
                yield return child;
        }
    }

    private static IEnumerable<(string Table, string Column)> ExtractColumnReferences(string expression)
    {
        foreach (Match match in DaxColumnReference().Matches(expression))
            yield return (
                match.Groups["table"].Value.Replace("'", "", StringComparison.Ordinal),
                match.Groups["column"].Value);
    }

    [GeneratedRegex("'?(?<table>[A-Za-z_][A-Za-z0-9_ ]*)'?\\[(?<column>[^\\]]+)\\]")]
    private static partial Regex DaxColumnReference();
}
