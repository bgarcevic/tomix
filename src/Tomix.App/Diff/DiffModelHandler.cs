using Tomix.App.Diagnostics;
using Tomix.App.ModelObjects;
using Tomix.App.Models;
using Tomix.Core.Models;
using Tomix.Core.Properties;
using Tomix.Core.Results;

namespace Tomix.App.Diff;

public sealed class DiffModelHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public DiffModelHandler(IEnumerable<IModelProvider> providers)
        => _providers = providers.ToList();

    public async Task<TomixResult<DiffModelResult>> HandleAsync(
        DiffModelRequest request,
        CancellationToken cancellationToken)
    {
        // Resolve both sides before opening either one so invalid input never performs connection work.
        var leftProvider = _providers.ResolveSingleProvider(request.Left);
        if (leftProvider is null)
            return NoProvider(request.Left);

        var rightProvider = _providers.ResolveSingleProvider(request.Right);
        if (rightProvider is null)
            return NoProvider(request.Right);

        // Nested guards attribute connection failures to the side that actually failed.
        return await ProviderConnectionGuard.RunAsync(request.Left, async () =>
        {
            await using var leftSession = await leftProvider.OpenAsync(request.Left, cancellationToken);
            var leftSnapshot = await leftSession.GetSnapshotAsync(cancellationToken);

            return await ProviderConnectionGuard.RunAsync(request.Right, async () =>
            {
                await using var rightSession = await rightProvider.OpenAsync(request.Right, cancellationToken);
                var rightSnapshot = await rightSession.GetSnapshotAsync(cancellationToken);

                // Engine-computed state only exists once a database has been processed, so it is
                // only meaningful to ignore it when one side IS a live database. Comparing two
                // authored sources keeps reporting these differences: there, a data type present
                // on one side and absent on the other is an authored difference, not processing
                // state, and hiding it would report two unequal models as identical.
                var liveTargetInvolved = request.Left.IsRemote || request.Right.IsRemote;
                var changes = Compare(leftSnapshot, rightSnapshot, liveTargetInvolved);
                var summary = new DiffSummary(
                    Added: changes.Count(c => c.Action == "added"),
                    Removed: changes.Count(c => c.Action == "removed"),
                    Modified: changes.Count(c => c.Action == "modified"));
                var result = new DiffModelResult(changes.Count > 0, summary, changes);

                return TomixResult<DiffModelResult>.Ok(result, result.HasChanges ? 1 : 0);
            });
        });
    }

    private static TomixResult<DiffModelResult> NoProvider(ModelReference model)
        => TomixResult<DiffModelResult>.Fail(
            "TOMIX_NO_PROVIDER",
            $"No provider can open model: {model.Value}",
            exitCode: 2,
            hint: ModelSessionRunner.DefaultNoProviderHint);

    private static IReadOnlyList<DiffChange> Compare(
        ModelSnapshot left, ModelSnapshot right, bool ignoreEngineComputedState)
    {
        var leftObjects = ByKindAndPath(left);
        var rightObjects = ByKindAndPath(right);

        var changes = new List<DiffChange>();

        foreach (var path in leftObjects.Keys.Except(rightObjects.Keys, StringComparer.OrdinalIgnoreCase).Order())
        {
            var obj = leftObjects[path];
            if (ignoreEngineComputedState && IsEngineMaterialized(obj))
                continue;

            changes.Add(new DiffChange(
                "removed",
                ModelObjectProjection.KindLabel(obj.Kind),
                obj.Path));
        }

        foreach (var path in rightObjects.Keys.Except(leftObjects.Keys, StringComparer.OrdinalIgnoreCase).Order())
        {
            var obj = rightObjects[path];
            if (ignoreEngineComputedState && IsEngineMaterialized(obj))
                continue;

            changes.Add(new DiffChange(
                "added",
                ModelObjectProjection.KindLabel(obj.Kind),
                obj.Path));
        }

        foreach (var path in leftObjects.Keys.Intersect(rightObjects.Keys, StringComparer.OrdinalIgnoreCase).Order())
        {
            changes.AddRange(CompareProperties(
                leftObjects[path], rightObjects[path], ignoreEngineComputedState));
        }

        return changes;
    }

    /// <summary>
    /// Snapshot paths are not globally unique: a table's column, partition, and hierarchy can
    /// all share "Table/Name" (a default partition is named after its table, and a column often
    /// matches). Keying by kind keeps same-named siblings distinct and compares like with like.
    /// The indexer (last-wins) is deliberate so an unforeseen collision degrades to a slightly
    /// incomplete diff instead of failing the whole command.
    /// </summary>
    private static Dictionary<string, ModelObject> ByKindAndPath(ModelSnapshot snapshot)
    {
        var objects = new Dictionary<string, ModelObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in ModelObjectProjection.Flatten(snapshot))
            objects[$"{ModelObjectProjection.KindLabel(obj.Kind)}:{obj.Path}"] = obj;
        return objects;
    }

    /// <summary>
    /// A calculated table's columns are materialized by the engine when the table's expression
    /// is evaluated; source files only carry the expression. Against a live database, one being
    /// absent therefore means "not processed yet", not "will be added/removed" — an authored
    /// change to the columns surfaces through the table's partition expression instead. Columns
    /// present on both sides still have their properties compared.
    /// </summary>
    private static bool IsEngineMaterialized(ModelObject obj)
        => obj.Kind == ModelObjectKind.Column
           && obj.Property(PropertyBagKeys.ColumnType) == "CalculatedTableColumn";

    private static IEnumerable<DiffChange> CompareProperties(
        ModelObject left, ModelObject right, bool ignoreEngineComputedState)
    {
        foreach (var (name, oldValue, newValue) in Properties(left, right, ignoreEngineComputedState))
        {
            if (Equals(oldValue, newValue))
                continue;

            yield return new DiffChange(
                "modified",
                $"{ModelObjectProjection.KindLabel(left.Kind)}/{left.Path}",
                name,
                oldValue,
                newValue);
        }
    }

    private static IEnumerable<(string Name, object? OldValue, object? NewValue)> Properties(
        ModelObject left,
        ModelObject right,
        bool ignoreEngineComputedState)
    {
        yield return ("Name", left.Name, right.Name);
        yield return ("Kind", ModelObjectProjection.KindLabel(left.Kind), ModelObjectProjection.KindLabel(right.Kind));
        yield return ("Detail", left.Detail, right.Detail);
        yield return ("Expression", NormalizeExpression(left.Expression), NormalizeExpression(right.Expression));
        yield return ("Description", left.Description, right.Description);
        yield return ("IsHidden", left.Hidden, right.Hidden);

        if (left.Kind != right.Kind)
            yield break;

        foreach (var descriptor in ModelPropertyCatalog.For(left.Kind))
        {
            if (!descriptor.Diffable)
                continue;

            var oldValue = descriptor.Value(left);
            var newValue = descriptor.Value(right);

            // A measure's data type is computed by the engine when the model is processed; an
            // unprocessed source (a TMDL folder or .bim file) reports it as absent. Against a
            // live database, absent-vs-present is processing state rather than an authored
            // change, so the type only diffs when both sides carry a computed value.
            if (ignoreEngineComputedState
                && descriptor.JsonKey == "dataType"
                && (oldValue is null or "" || newValue is null or ""))
                continue;

            yield return (descriptor.Header, oldValue, newValue);
        }
    }

    private static string? NormalizeExpression(string? value)
    {
        if (value is null)
            return null;

        var lines = value
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.TrimEnd())
            .ToList();

        while (lines.Count > 0 && lines[0].Length == 0)
            lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        if (lines.Count == 0)
            return "";

        var indent = lines.Where(line => line.Length > 0)
            .Select(line => line.Length - line.TrimStart().Length)
            .DefaultIfEmpty(0)
            .Min();

        return string.Join('\n', lines.Select(line => line.Length >= indent ? line[indent..] : line));
    }
}
