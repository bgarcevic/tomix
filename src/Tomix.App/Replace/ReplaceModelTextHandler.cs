using Tomix.App.Mutations;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Replace;

public sealed class ReplaceModelTextHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public ReplaceModelTextHandler(IEnumerable<IModelProvider> providers)
        => _providers = providers.ToList();

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
            request.NoSync);

        return await MutationRunner.RunAsync(
            _providers, request.Model, options, "replace",
            async (mutator, _, context) =>
            {
                var persist = context.Mode is MutationMode.Save or MutationMode.Stage;

                var replace = mutator.ReplaceText(new ModelReplaceRequest(
                    request.Pattern,
                    request.Replacement,
                    request.Scope,
                    request.Regex,
                    request.CaseSensitive,
                    Apply: persist));

                if (!persist)
                {
                    return (false, "",
                        _ => new ReplaceModelTextResult(
                            request.Pattern, request.Replacement,
                            DryRun: true, replace.ChangeCount, replace.Previews, Saved: null));
                }

                return (replace.ChangeCount > 0, $"replace {request.Pattern}",
                    outcome => new ReplaceModelTextResult(
                        request.Pattern, request.Replacement,
                        DryRun: null, replace.ChangeCount, Previews: null,
                        outcome.Saved, outcome.Staged,
                        outcome.Synced, outcome.SyncTarget, outcome.SyncWarning));
            },
            new ReplaceModelTextResult(
                request.Pattern, request.Replacement,
                DryRun: null, ChangeCount: 0, Previews: null, Saved: false),
            cancellationToken);
    }
}
