using Tomix.App.ModelObjects;
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
        var provider = _providers.FirstOrDefault(p => p.CanOpen(request.Model));

        if (provider is null)
            return TomixResult<GetModelResult>.Fail(
                code: "TOMIX_NO_PROVIDER",
                message: $"No provider can open model: {request.Model.Value}",
                exitCode: 2,
                hint: "Supported formats: TMDL folder, .bim file. For remote models, use --server and --database.");

        await using var session = await provider.OpenAsync(request.Model, cancellationToken);
        var snapshot = await session.GetSnapshotAsync(cancellationToken);
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

        var obj = matches[0];
        var properties = ModelPropertyCatalog.Project(obj);

        if (!string.IsNullOrWhiteSpace(request.Query))
            properties = ProjectSingleProperty(properties, request.Query);

        return TomixResult<GetModelResult>.Ok(new GetModelResult(
            ModelObjectProjection.KindLabel(obj.Kind),
            obj.Path,
            properties,
            obj));
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
