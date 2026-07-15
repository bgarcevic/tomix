using System.Diagnostics;
using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;

namespace Tomix.Provider.Tom;

/// <summary>
/// Runs TOM's server-side <see cref="Table.ApplyRefreshPolicy(DateTime, bool, int)"/>: diffs the
/// partition scheme the policy expects for the effective date against the table's existing
/// partitions and creates/merges/drops as needed. Refresh=false is the bootstrap pattern:
/// partition definitions are created without loading data.
/// </summary>
internal static class TomRefreshPolicyApplier
{
    public static Task<RefreshPolicyApplyResult> ApplyAsync(
        Server server,
        Database database,
        RefreshPolicyApplyRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var table = database.Model.Tables
            .FirstOrDefault(t => string.Equals(t.Name, request.Table, StringComparison.OrdinalIgnoreCase))
            ?? throw new ObjectNotFoundException(
                $"Table not found: {request.Table}",
                hint: "Run 'tx ls tables' to list tables.");

        if (table.RefreshPolicy is null)
            throw new RefreshPolicyNotFoundException(
                $"Table '{table.Name}' has no incremental refresh policy. Use 'tx incremental-refresh set' to create one.");

        var effectiveDate = request.EffectiveDate?.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today;

        var stopwatch = Stopwatch.StartNew();
        var results = table.ApplyRefreshPolicy(effectiveDate, request.Refresh, request.MaxParallelism ?? 0);
        stopwatch.Stop();

        return Task.FromResult(new RefreshPolicyApplyResult(
            server.Name ?? "",
            string.IsNullOrWhiteSpace(database.Name) ? database.ID : database.Name,
            table.Name,
            DateOnly.FromDateTime(effectiveDate),
            request.Refresh,
            DescribeOperations(results),
            stopwatch.ElapsedMilliseconds));
    }

    private static IReadOnlyList<string> DescribeOperations(IReadOnlyList<ModelOperationResult> results)
    {
        var operations = new List<string>();
        foreach (var result in results)
        {
            var impact = result.Impact;
            if (impact is null || impact.IsEmpty)
                continue;

            operations.AddRange(impact.AddedObjects.Select(o => $"created {Describe(o)}"));
            operations.AddRange(impact.RemovedObjects.Select(o => $"removed {Describe(o)}"));
            operations.AddRange(impact.PropertyChanges
                .Select(c => c.Object)
                .Distinct()
                .Select(o => $"changed {Describe(o)}"));
        }

        return operations;
    }

    private static string Describe(MetadataObject metadataObject)
        => metadataObject is NamedMetadataObject named
            ? $"{named.ObjectType.ToString().ToLowerInvariant()} '{named.Name}'"
            : metadataObject.ObjectType.ToString().ToLowerInvariant();
}
