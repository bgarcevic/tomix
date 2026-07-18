using Tomix.App.Diagnostics;
using Tomix.App.Mutations;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.IncrementalRefresh;

/// <summary>
/// Reads a table's incremental refresh policy. The result is the provider-agnostic
/// <see cref="RefreshPolicyInfo"/> with validation issues attached, so 'show' surfaces the
/// same findings 'set' validates against.
/// </summary>
public sealed class ShowRefreshPolicyHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public ShowRefreshPolicyHandler(IEnumerable<IModelProvider> providers)
        => _providers = providers.ToList();

    public async Task<TomixResult<RefreshPolicyInfo>> HandleAsync(
        ShowRefreshPolicyRequest request,
        CancellationToken cancellationToken)
    {
        var provider = _providers.ResolveSingle(request.Model);
        if (provider is null)
            return TomixResult<RefreshPolicyInfo>.Fail(
                "TOMIX_NO_PROVIDER",
                $"No provider can open model: {request.Model.Value}",
                exitCode: 2,
                hint: "Supported formats: TMDL folder, .bim file. For remote models, use --server and --database.");

        // Open + read inside the try: for a remote endpoint OpenAsync can throw on auth or
        // connection setup, and those must surface as actionable diagnostics rather than an
        // unhandled exception. (A local ModelLoadException is left to the top-level handler,
        // mirroring the other read commands.)
        return await ProviderConnectionGuard.RunAsync(request.Model, async () =>
        {
            // ObjectNotFound/NotSupported stay inside the guard so they keep their specific
            // diagnostics even on remote models, as the previous flat catch order guaranteed.
            try
            {
                await using var session = await provider.OpenAsync(request.Model, cancellationToken);
                if (session is not IModelMutationSession mutator)
                    return TomixResult<RefreshPolicyInfo>.Fail(
                        "TOMIX_MUTATION_UNSUPPORTED_PROVIDER",
                        $"Provider cannot read refresh policies for: {request.Model.Value}");

                var policy = MutationCapabilities.RequireRefreshPolicies(mutator).GetRefreshPolicy(request.Table);
                if (policy is null)
                    return TomixResult<RefreshPolicyInfo>.Fail(
                        "TOMIX_REFRESH_POLICY_NOT_FOUND",
                        $"Table '{request.Table}' has no incremental refresh policy.",
                        exitCode: 1,
                        hint: "Use 'tx incremental-refresh set' to create one.");

                return TomixResult<RefreshPolicyInfo>.Ok(policy);
            }
            catch (ObjectNotFoundException ex)
            {
                return TomixResult<RefreshPolicyInfo>.Fail(
                    "TOMIX_OBJECT_NOT_FOUND", ex.Message, exitCode: 1, hint: ex.Hint);
            }
            catch (NotSupportedException ex)
            {
                return TomixResult<RefreshPolicyInfo>.Fail("TOMIX_MUTATION_UNSUPPORTED", ex.Message);
            }
        });
    }
}
