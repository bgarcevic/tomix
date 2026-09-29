using Tomix.App.Dax;
using Tomix.App.ModelObjects;
using Tomix.App.Models;
using Tomix.Core.Models;
using Tomix.Core.Properties;
using Tomix.Core.Results;

namespace Tomix.App.Get;

public sealed class GetModelHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public GetModelHandler(IEnumerable<IModelProvider> providers)
        => _providers = providers.ToList();

    public async Task<TomixResult<GetModelResult>> HandleAsync(
        GetModelRequest request,
        CancellationToken cancellationToken)
    {
        return await ModelSessionRunner.RunAsync(_providers, request.Model, async session =>
        {
            var snapshot = await session.GetSnapshotAsync(cancellationToken);
            var measureNames = DaxModelNames.MeasureNames(snapshot);

            // The model root is not a snapshot object: "." synthesizes one from the
            // snapshot's model-level properties so get can read back what set accepts
            // on the root (culture, compatibility level, and friends).
            if (request.Path.Trim().Trim('/') == ".")
                return Project(ModelRoot(snapshot), request.Query, measureNames);

            var matches = ModelObjectLookup.Find(snapshot, request.Path, request.Type).ToList();

            if (matches.Count == 0)
                return TomixResult<GetModelResult>.Fail(
                    code: "TOMIX_OBJECT_NOT_FOUND",
                    message: ModelObjectLookup.NotFoundMessage(request.Path),
                    exitCode: 1,
                    hint: "Run 'tx ls' to list available objects, or 'tx ls Sa*' to filter.");

            if (matches.Count > 1)
                return TomixResult<GetModelResult>.Fail(
                    code: "TOMIX_OBJECT_AMBIGUOUS",
                    message: AmbiguousMatchMessage.For(request.Path, matches),
                    exitCode: 1,
                    hint: AmbiguousMatchMessage.Hint);

            return Project(matches[0], request.Query, measureNames);
        }, cancellationToken);
    }

    private static ModelObject ModelRoot(ModelSnapshot snapshot)
        => new(
            snapshot.Name,
            ModelObjectKind.Model,
            ".",
            Detail: null,
            Expression: null,
            Description: snapshot.Description,
            Hidden: false,
            SourceColumn: null,
            Children: [],
            Properties: snapshot.Properties);

    private static TomixResult<GetModelResult> Project(ModelObject obj, string? query, IReadOnlySet<string> measureNames)
    {
        var properties = ModelPropertyCatalog.Project(obj);
        var kind = ModelObjectProjection.KindLabel(obj.Kind);

        if (!string.IsNullOrWhiteSpace(query))
        {
            var match = properties.FirstOrDefault(p =>
                string.Equals(p.Key, query, StringComparison.OrdinalIgnoreCase));
            if (match.Key is not null)
                properties = new Dictionary<string, object?> { [match.Key] = match.Value };
            // An annotation or translation that was never set reads back as null: the token is
            // well-formed, the object just carries no value for it.
            else if (IsBagToken(query))
                properties = new Dictionary<string, object?> { [query] = null };
            else
                return TomixResult<GetModelResult>.Fail(
                    code: "TOMIX_PROPERTY_NOT_FOUND",
                    message: $"{kind} '{obj.Path}' has no property '{query}'.",
                    exitCode: 1,
                    hint: PropertyHint(query, properties.Keys.Where(k => !IsBagToken(k)).ToList()));
        }

        return TomixResult<GetModelResult>.Ok(new GetModelResult(
            kind,
            obj.Path,
            properties,
            obj,
            measureNames));
    }

    private static bool IsBagToken(string key)
        => key.StartsWith("annotation:", StringComparison.OrdinalIgnoreCase)
           || key.StartsWith("translation:", StringComparison.OrdinalIgnoreCase);

    private static string PropertyHint(string query, IReadOnlyList<string> keys)
    {
        var closest = keys
            .Select(key => (Key: key, Distance: EditDistance(query.ToLowerInvariant(), key.ToLowerInvariant())))
            .Where(candidate => candidate.Distance <= Math.Max(2, query.Length / 3))
            .OrderBy(candidate => candidate.Distance)
            .Select(candidate => candidate.Key)
            .FirstOrDefault();
        return closest is null
            ? $"Properties: {string.Join(", ", keys)}."
            : $"Did you mean '{closest}'?";
    }

    private static int EditDistance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = current;
        }

        return previous[b.Length];
    }

}
