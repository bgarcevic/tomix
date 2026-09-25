using Tomix.App.Mutations;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Replace;

public sealed class ReplaceModelTextHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly MutationStores _stores;

    public ReplaceModelTextHandler(IEnumerable<IModelProvider> providers, MutationStores stores)
    {
        _providers = providers.ToList();
        _stores = stores;
    }

    public async Task<TomixResult<ReplaceModelTextResult>> HandleAsync(
        ReplaceModelTextRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.Pattern))
            return TomixResult<ReplaceModelTextResult>.Fail(
                "TOMIX_REPLACE_PATTERN_REQUIRED",
                "A pattern is required.",
                exitCode: 2);

        var options = new MutationOptions(
            request.Save && !request.DryRun,
            request.DryRun ? null : request.SaveTo,
            request.Stage && !request.DryRun,
            request.Revert,
            request.Serialization,
            request.Force,
            request.Overwrite,
            request.NoSync,
            DryRun: request.DryRun);

        return await MutationRunner.RunAsync(
            _providers, request.Model, options, "replace", _stores,
            async (mutator, _, context) =>
            {
                var persist = context.Mode is MutationMode.Save or MutationMode.Stage;

                var replace = mutator.ReplaceText(new ModelReplaceRequest(
                    request.Pattern,
                    request.Replacement,
                    request.Scope,
                    request.Regex,
                    request.CaseSensitive,
                    Apply: persist,
                    Type: request.Type));

                // Without --save/--stage nothing is applied, only previewed. Reporting it as a
                // change lets the lifecycle label it a preview or dry run; that mode persists nothing.
                if (!persist)
                {
                    return (true, "",
                        outcome => new ReplaceModelTextResult(
                            request.Pattern, request.Replacement, replace.ChangeCount, replace.Previews)
                        { Outcome = outcome });
                }

                return (replace.ChangeCount > 0, $"replace {request.Pattern}",
                    outcome => new ReplaceModelTextResult(
                        request.Pattern, request.Replacement, replace.ChangeCount, Previews: null)
                    { Outcome = outcome });
            },
            outcome => new ReplaceModelTextResult(
                request.Pattern, request.Replacement, ChangeCount: 0, Previews: null)
            { Outcome = outcome },
            cancellationToken);
    }
}
