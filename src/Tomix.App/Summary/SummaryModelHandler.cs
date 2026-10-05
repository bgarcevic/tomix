using Tomix.App.Models;
using Tomix.Core.Models;
using Tomix.Core.Properties;
using Tomix.Core.Results;

namespace Tomix.App.Summary;

/// <summary>
/// The one-screen answer to "what is this model?": where it lives, its model-level settings, and
/// how many of each object it holds. Counts from <see cref="ModelSummary"/> match what the provider
/// reports elsewhere (connect); the remaining counts come from the snapshot.
/// </summary>
public sealed class SummaryModelHandler
{
    private readonly IModelSessionSource _sessions;

    public SummaryModelHandler(IEnumerable<IModelProvider> providers)
        : this(new OneShotSessionSource(providers))
    {
    }

    public SummaryModelHandler(IModelSessionSource sessions)
        => _sessions = sessions;

    public async Task<TomixResult<SummaryModelResult>> HandleAsync(
        SummaryModelRequest request,
        CancellationToken cancellationToken)
    {
        return await ModelSessionRunner.RunAsync(_sessions, request.Model, async session =>
        {
            var summary = await session.GetSummaryAsync(cancellationToken);
            var snapshot = await session.GetSnapshotAsync(cancellationToken);
            var tables = snapshot.Objects.Where(obj => obj.Kind == ModelObjectKind.Table).ToList();

            return TomixResult<SummaryModelResult>.Ok(new SummaryModelResult(
                summary.Name,
                summary.DatabaseName ?? (request.Model.IsRemote ? request.Model.Database : null),
                Source(request.Model),
                Format(request.Model),
                summary.CompatibilityLevel,
                Property(snapshot, PropertyBagKeys.Culture),
                Property(snapshot, PropertyBagKeys.DefaultMode),
                new SummaryCounts(
                    summary.Tables,
                    summary.Columns,
                    summary.Measures,
                    summary.Relationships,
                    summary.Roles,
                    Partitions: tables.Sum(table => table.Children.Count(child => child.Kind == ModelObjectKind.Partition)),
                    CalculationGroups: tables.Count(table => table.Children.Any(child => child.Kind == ModelObjectKind.CalculationItem)),
                    Perspectives: snapshot.Objects.Count(obj => obj.Kind == ModelObjectKind.Perspective),
                    Cultures: snapshot.Objects.Count(obj => obj.Kind == ModelObjectKind.Culture))));
        }, cancellationToken);
    }

    private static string Source(ModelReference model)
        => model.IsRemote ? model.Value : Path.GetFullPath(model.Value);

    /// <summary>
    /// The on-disk serialization of a local model: a .bim/.tmsl file, or a model folder holding
    /// model.bim, is bim; any other folder is TMDL. Remote models have none.
    /// </summary>
    private static string? Format(ModelReference model)
    {
        if (!model.IsLocalPath)
            return null;

        var extension = Path.GetExtension(model.Value);
        if (extension.Equals(".bim", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tmsl", StringComparison.OrdinalIgnoreCase))
            return "bim";

        return File.Exists(Path.Combine(model.Value, "model.bim")) ? "bim" : "tmdl";
    }

    private static string? Property(ModelSnapshot snapshot, string key)
        => snapshot.Properties?.TryGetValue(key, out var value) == true && !string.IsNullOrEmpty(value)
            ? value
            : null;
}
