using Tomix.App.Mutations;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Add;

public sealed class AddModelObjectHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly MutationStores _stores;

    public AddModelObjectHandler(IEnumerable<IModelProvider> providers, MutationStores stores)
    {
        _providers = providers.ToList();
        _stores = stores;
    }

    public async Task<TomixResult<AddModelObjectResult>> HandleAsync(
        AddModelObjectRequest request,
        CancellationToken cancellationToken)
    {
        var options = new MutationOptions(
            request.Save, request.SaveTo, request.Stage, request.Revert, request.Serialization, request.Force, request.Overwrite, request.NoSync, DryRun: request.DryRun);

        return await MutationRunner.RunAsync(
            _providers, request.Model, options, "add", _stores,
            async (mutator, _, _) =>
            {
                var mutation = mutator.AddObject(new ModelObjectAddRequest(
                    request.Path,
                    request.Type,
                    request.Value,
                    request.Properties,
                    request.IfNotExists,
                    request.Columns,
                    request.Mode,
                    request.Source,
                    request.Endpoint,
                    request.ConnectionString,
                    request.SourceTable,
                    request.SourceDatabase,
                    request.PartitionExpression,
                    request.SourceType,
                    request.SourceSchema,
                    request.RangeStart,
                    request.RangeEnd,
                    request.RangeGranularity));

                // Changed == false is only reachable via --if-not-exists (everything else throws).
                var existing = mutation.Changed ? null : mutation.Path;
                return (mutation.Changed, $"add {mutation.Path}",
                    outcome => new AddModelObjectResult(mutation.Changed ? mutation.Path : null, existing) { Outcome = outcome });
            },
            outcome => new AddModelObjectResult(null) { Outcome = outcome },
            cancellationToken);
    }
}
