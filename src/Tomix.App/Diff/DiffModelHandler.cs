using Tomix.App.ModelObjects;
using Tomix.Core.Models;
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
        var leftProvider = _providers.FirstOrDefault(p => p.CanOpen(request.Left));
        if (leftProvider is null)
            return TomixResult<DiffModelResult>.Fail(
                code: "TOMIX_NO_PROVIDER",
                message: $"No provider can open model: {request.Left.Value}",
                exitCode: 2,
                hint: "Supported formats: TMDL folder, .bim file. For remote models, use --server and --database.");

        var rightProvider = _providers.FirstOrDefault(p => p.CanOpen(request.Right));
        if (rightProvider is null)
            return TomixResult<DiffModelResult>.Fail(
                code: "TOMIX_NO_PROVIDER",
                message: $"No provider can open model: {request.Right.Value}",
                exitCode: 2,
                hint: "Supported formats: TMDL folder, .bim file. For remote models, use --server and --database.");

        await using var leftSession = await leftProvider.OpenAsync(request.Left, cancellationToken);
        await using var rightSession = await rightProvider.OpenAsync(request.Right, cancellationToken);

        var leftSnapshot = await leftSession.GetSnapshotAsync(cancellationToken);
        var rightSnapshot = await rightSession.GetSnapshotAsync(cancellationToken);

        var changes = Compare(leftSnapshot, rightSnapshot);
        var summary = new DiffSummary(
            Added: changes.Count(c => c.Action == "added"),
            Removed: changes.Count(c => c.Action == "removed"),
            Modified: changes.Count(c => c.Action == "modified"));
        var result = new DiffModelResult(changes.Count > 0, summary, changes);

        return TomixResult<DiffModelResult>.Ok(result, result.HasChanges ? 1 : 0);
    }

    private static IReadOnlyList<DiffChange> Compare(ModelSnapshot left, ModelSnapshot right)
    {
        var leftObjects = ModelObjectProjection
            .Flatten(left)
            .ToDictionary(o => o.Path, StringComparer.OrdinalIgnoreCase);
        var rightObjects = ModelObjectProjection
            .Flatten(right)
            .ToDictionary(o => o.Path, StringComparer.OrdinalIgnoreCase);

        var changes = new List<DiffChange>();

        foreach (var path in leftObjects.Keys.Except(rightObjects.Keys, StringComparer.OrdinalIgnoreCase).Order())
        {
            var obj = leftObjects[path];
            changes.Add(new DiffChange(
                "removed",
                ModelObjectProjection.KindLabel(obj.Kind),
                obj.Path));
        }

        foreach (var path in rightObjects.Keys.Except(leftObjects.Keys, StringComparer.OrdinalIgnoreCase).Order())
        {
            var obj = rightObjects[path];
            changes.Add(new DiffChange(
                "added",
                ModelObjectProjection.KindLabel(obj.Kind),
                obj.Path));
        }

        foreach (var path in leftObjects.Keys.Intersect(rightObjects.Keys, StringComparer.OrdinalIgnoreCase).Order())
        {
            changes.AddRange(CompareProperties(leftObjects[path], rightObjects[path]));
        }

        return changes;
    }

    private static IEnumerable<DiffChange> CompareProperties(ModelObject left, ModelObject right)
    {
        foreach (var (name, oldValue, newValue) in Properties(left, right))
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
        ModelObject right)
    {
        yield return ("Name", left.Name, right.Name);
        yield return ("Kind", ModelObjectProjection.KindLabel(left.Kind), ModelObjectProjection.KindLabel(right.Kind));
        yield return ("Detail", left.Detail, right.Detail);
        yield return ("Expression", NormalizeExpression(left.Expression), NormalizeExpression(right.Expression));
        yield return ("Description", left.Description, right.Description);
        yield return ("IsHidden", left.Hidden, right.Hidden);
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
