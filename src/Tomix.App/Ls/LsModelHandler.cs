using Tomix.App.Models;
using Tomix.Core.Models;
using Tomix.Core.Paths;
using Tomix.Core.Properties;
using Tomix.Core.Results;

namespace Tomix.App.Ls;

public sealed class LsModelHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public LsModelHandler(IEnumerable<IModelProvider> providers)
        => _providers = providers.ToList();

    public async Task<TomixResult<LsModelResult>> HandleAsync(
        LsModelRequest request,
        CancellationToken cancellationToken)
    {
        return await ModelSessionRunner.RunAsync(_providers, request.Model, async session =>
        {
            var snapshot = await session.GetSnapshotAsync(cancellationToken);

            var matches = ModelObjectSelector
                .Select(snapshot, request.PathFilter, request.Type)
                .Select(o => new LsObject(
                    o.Path, o.Name, o.Kind, o.Detail, o.Expression, o.Description, o.Hidden,
                    o.SourceColumn,
                    o.Children.GroupBy(c => c.Kind).ToDictionary(g => g.Key, g => g.Count()),
                    ModelPropertyCatalog.Project(o)))
                .ToList();

            return TomixResult<LsModelResult>.Ok(
                new LsModelResult(snapshot.Name, snapshot.CompatibilityLevel, matches));
        }, cancellationToken);
    }
}
