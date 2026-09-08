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
                return TomixResult<GetModelResult>.Ok(Project(ModelRoot(snapshot), request.Query, measureNames));

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

            return TomixResult<GetModelResult>.Ok(Project(matches[0], request.Query, measureNames));
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

    private static GetModelResult Project(ModelObject obj, string? query, IReadOnlySet<string> measureNames)
    {
        var properties = ModelPropertyCatalog.Project(obj);

        if (!string.IsNullOrWhiteSpace(query))
            properties = ProjectSingleProperty(properties, query);

        return new GetModelResult(
            ModelObjectProjection.KindLabel(obj.Kind),
            obj.Path,
            properties,
            obj,
            measureNames);
    }

    private static IReadOnlyDictionary<string, object?> ProjectSingleProperty(
        IReadOnlyDictionary<string, object?> properties,
        string query)
    {
        var match = properties.FirstOrDefault(p =>
            string.Equals(p.Key, query, StringComparison.OrdinalIgnoreCase));

        return match.Key is null
            ? new Dictionary<string, object?> { [query] = null }
            : new Dictionary<string, object?> { [match.Key] = match.Value };
    }

}
