using Tomix.App.Mutations;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Add;

public sealed class AddModelObjectHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public AddModelObjectHandler(IEnumerable<IModelProvider> providers)
        => _providers = providers.ToList();

    public async Task<TomixResult<AddModelObjectResult>> HandleAsync(
        AddModelObjectRequest request,
        CancellationToken cancellationToken)
    {
        var options = new MutationOptions(
            request.Save, request.SaveTo, request.Stage, request.Revert, request.Serialization, request.Force, request.NoSync);

        return await MutationRunner.RunAsync(
            _providers, request.Model, options, "add",
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
                    request.SourceType));

                var added = mutation.Changed ? mutation.Path : (object)false;
                return (mutation.Changed, $"add {mutation.Path}",
                    outcome => new AddModelObjectResult(added, outcome.Saved, outcome.Staged, outcome.Synced, outcome.SyncTarget, outcome.SyncWarning));
            },
            new AddModelObjectResult(false, false, null),
            cancellationToken);
    }
}
