using Tomix.App.ModelObjects;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Deps;

public sealed class DepsModelHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public DepsModelHandler(IEnumerable<IModelProvider> providers)
        => _providers = providers.ToList();

    public async Task<TomixResult<DepsModelResult>> HandleAsync(
        DepsModelRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.Unused && string.IsNullOrWhiteSpace(request.Path))
            return TomixResult<DepsModelResult>.Fail(
                code: "TOMIX_DEPS_PATH_REQUIRED",
                message: "A dependency path is required unless --unused is specified.",
                exitCode: 2);

        var provider = _providers.ResolveSingle(request.Model);

        if (provider is null)
            return TomixResult<DepsModelResult>.Fail(
                code: "TOMIX_NO_PROVIDER",
                message: $"No provider can open model: {request.Model.Value}",
                exitCode: 2,
                hint: "Supported formats: TMDL folder, .bim file. For remote models, use --server and --database.");

        await using var session = await provider.OpenAsync(request.Model, cancellationToken);
        var snapshot = await session.GetSnapshotAsync(cancellationToken);
        var graph = DependencyGraph.FromSnapshot(snapshot);

        if (request.Unused)
            return TomixResult<DepsModelResult>.Ok(new DepsModelResult(
                Path: "",
                Type: "",
                Upstream: [],
                Downstream: [],
                Unused: graph.Unused(request.HiddenOnly)));

        var targetMatches = ModelObjectLookup.Find(snapshot, request.Path!, request.Type).ToList();

        if (targetMatches.Count == 0)
            return TomixResult<DepsModelResult>.Fail(
                code: "TOMIX_OBJECT_NOT_FOUND",
                message: ModelObjectLookup.NotFoundMessage(request.Path!),
                exitCode: 1,
                hint: "Run 'tx ls' to list available objects, or 'tx ls Sa*' to filter.");

        if (targetMatches.Count > 1)
            return TomixResult<DepsModelResult>.Fail(
                code: "TOMIX_OBJECT_AMBIGUOUS",
                message: AmbiguousMatchMessage.For(request.Path!, targetMatches),
                exitCode: 1,
                hint: AmbiguousMatchMessage.Hint);

        var target = targetMatches[0];
        var maxDepth = request.MaxDepth > 0 ? request.MaxDepth : int.MaxValue;

        var upstream = request.Deep
            ? graph.Deep(target, upstream: true, maxDepth)
            : graph.DirectUpstream(target);
        var downstream = request.Deep
            ? graph.Deep(target, upstream: false, maxDepth)
            : graph.DirectDownstream(target);

        if (request.UpstreamOnly)
            downstream = [];
        if (request.DownstreamOnly)
            upstream = [];

        return TomixResult<DepsModelResult>.Ok(new DepsModelResult(
            target.Path,
            ModelObjectProjection.KindLabel(target.Kind),
            upstream,
            downstream));
    }
}
