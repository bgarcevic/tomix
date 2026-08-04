using Tomix.App.Bpa;
using Tomix.App.Diff;
using Tomix.App.Models;
using Tomix.App.State;
using Tomix.Core.Authentication;
using Tomix.Core.Bpa;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Deploy;

public sealed class DeployModelHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly CliStateStore _state;
    private readonly Func<CliConnectionState?> _resolveSession;
    private readonly HttpClient? _httpClient;

    public DeployModelHandler(
        IEnumerable<IModelProvider> providers,
        CliStateStore state,
        Func<CliConnectionState?>? sessionOverride = null,
        HttpClient? httpClient = null)
    {
        _providers = providers.ToList();
        _state = state;
        _resolveSession = sessionOverride ?? state.LoadCurrentSession;
        _httpClient = httpClient;
    }

    public async Task<TomixResult<DeployModelResult>> HandleAsync(
        DeployModelRequest request,
        CancellationToken cancellationToken)
    {
        var deployOptions = request.DeployOptions ?? ModelDeployOptions.Preserve;
        if (deployOptions.DeployRoleMembers && !deployOptions.DeployRoles)
            return TomixResult<DeployModelResult>.Fail(
                "TOMIX_DEPLOY_INVALID_FLAGS",
                "--deploy-role-members requires --deploy-roles: members cannot be overwritten while the target's role definitions are preserved.",
                exitCode: 2);

        if (deployOptions.DeployPolicyPartitions && !deployOptions.DeployPartitions)
            return TomixResult<DeployModelResult>.Fail(
                "TOMIX_DEPLOY_INVALID_FLAGS",
                "--deploy-policy-partitions requires --deploy-partitions.",
                exitCode: 2);

        if (request.Model.Value.Length == 0)
            return TomixResult<DeployModelResult>.Fail(
                "TOMIX_NO_MODEL",
                "No model specified. Use --model <path>, --server <url> --database <name>, or set an active connection with 'tx connect'.",
                exitCode: 2,
                hint: "Specify a model path or use --recent.");

        var provider = _providers.ResolveSingleProvider(request.Model);
        if (provider is null)
            return TomixResult<DeployModelResult>.Fail(
                "TOMIX_NO_PROVIDER",
                $"No provider can open model: {request.Model.Value}",
                exitCode: 2,
                hint: "Supported formats: TMDL folder, .bim file. For remote models, use --server and --database.");

        await using var session = await provider.OpenAsync(request.Model, cancellationToken);

        if (!request.SkipBpa)
        {
            var bpaResult = await RunBpaGate(session, request, cancellationToken);
            if (bpaResult is not null)
                return bpaResult;
        }

        if (session is not IModelDeploySession deployer)
            return TomixResult<DeployModelResult>.Fail(
                "TOMIX_DEPLOY_UNSUPPORTED",
                $"Provider cannot deploy model: {request.Model.Value}",
                exitCode: 1);

        var (server, database) = ResolveTarget(request, _state, _resolveSession);

        if (string.IsNullOrWhiteSpace(server))
            return TomixResult<DeployModelResult>.Fail(
                "TOMIX_DEPLOY_NO_TARGET",
                "No target workspace specified. Use -s/--server or set an active connection with 'tx connect'.",
                exitCode: 2,
                hint: "Specify --workspace or --server and --database.");

        var deployRequest = new ModelDeployRequest(
            server,
            database,
            request.CreateOnly,
            request.Force,
            deployOptions);

        if (request.DryRun)
        {
            DiffModelResult? diff = null;
            string? diffError = null;

            if (!string.IsNullOrWhiteSpace(server) && !string.IsNullOrWhiteSpace(database))
            {
                // Normalize so a bare workspace name (-s MyWorkspace) becomes a real endpoint,
                // matching what the deployer does internally. The reference has to read as
                // remote for the diff to treat the target as a processed database.
                var remoteRef = ModelReference.Remote(
                    ModelReference.NormalizeEndpoint(server), database);
                var diffHandler = new DiffModelHandler(_providers);
                // Target first: the dry run answers "what will this deploy change on the
                // target", so added/removed/old→new must read in the deploy's direction.
                var diffResult = await diffHandler.HandleAsync(
                    new DiffModelRequest(remoteRef, request.Model),
                    cancellationToken);

                if (diffResult.Success)
                    diff = diffResult.Data;
                else
                    // Keep the reason: "target unreachable" and "diff failed" are different
                    // situations and the dry-run output should not conflate them.
                    diffError = diffResult.Diagnostics.Count > 0 ? diffResult.Diagnostics[0].Message : null;
            }

            return TomixResult<DeployModelResult>.Ok(new DeployModelResult(
                server, database ?? request.Model.Value, "dry-run", null, null, null, diff, diffError));
        }

        if (!string.IsNullOrWhiteSpace(request.XmlaOutput))
        {
            string script;
            try
            {
                // Preservation options require reading the target so the script matches what a
                // real deploy would execute; a full deploy is scripted offline.
                script = await deployer.GenerateScriptAsync(deployRequest, cancellationToken);
            }
            catch (AuthenticationRequiredException ex)
            {
                return TomixResult<DeployModelResult>.Fail("TOMIX_AUTH_REQUIRED", ex.Message, exitCode: 1,
                    hint: "Run 'tx auth login' to authenticate, or use --deploy-full to script without reading the target.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not ModelLoadException)
            {
                return TomixResult<DeployModelResult>.Fail(
                    "TOMIX_DEPLOY_FAILED",
                    $"Cannot read target '{server}' to build the script: {ex.InnerException?.Message ?? ex.Message}",
                    exitCode: 1,
                    hint: "The script must reflect preserved target objects. Use --deploy-full to script without reading the target.");
            }

            var scriptPath = request.XmlaOutput;

            if (scriptPath == "-")
                return TomixResult<DeployModelResult>.Ok(new DeployModelResult(
                    server, database ?? request.Model.Value, "script", null, "-", script));

            var fullPath = Path.GetFullPath(scriptPath);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(fullPath, script, cancellationToken).ConfigureAwait(false);
            return TomixResult<DeployModelResult>.Ok(new DeployModelResult(
                server, database ?? request.Model.Value, "script", null, fullPath, null));
        }

        try
        {
            var result = await deployer.DeployAsync(deployRequest, cancellationToken);
            return TomixResult<DeployModelResult>.Ok(new DeployModelResult(
                result.Server, result.Database, result.Status, result.DurationMs, null, null));
        }
        catch (AuthenticationRequiredException ex)
        {
            return TomixResult<DeployModelResult>.Fail("TOMIX_AUTH_REQUIRED", ex.Message, exitCode: 1,
                hint: "Run 'tx auth login' to authenticate, or use --auth spn for service principal.");
        }
        catch (InvalidOperationException ex)
        {
            return TomixResult<DeployModelResult>.Fail("TOMIX_DEPLOY_FAILED", ex.Message, exitCode: 1,
                hint: "Check that the target workspace exists and you have deploy permissions.");
        }
        // ModelLoadException stays unhandled: the source model being unloadable is not a deploy
        // failure — the CLI's top-level handler reports it as TOMIX_MODEL_LOAD_FAILED (exit 2).
        catch (Exception ex) when (ex is not OperationCanceledException and not ModelLoadException)
        {
            return TomixResult<DeployModelResult>.Fail("TOMIX_DEPLOY_FAILED", $"Deploy to '{server}' failed: {ex.InnerException?.Message ?? ex.Message}", exitCode: 1,
                hint: "Check that the target workspace exists and you have deploy permissions.");
        }
    }

    private async Task<TomixResult<DeployModelResult>?> RunBpaGate(
        IModelSession session,
        DeployModelRequest request,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BpaRule> rules;
        try
        {
            rules = await BpaRuleLoader
                .LoadRulesetAsync(null, _httpClient, cancellationToken)
                .ConfigureAwait(false);
            if (request.BpaRules is not null)
            {
                foreach (var file in request.BpaRules)
                {
                    if (!string.IsNullOrWhiteSpace(file))
                        rules =
                        [
                            .. rules,
                            .. await BpaRuleLoader
                                .LoadFromSourceAsync(file, _httpClient, cancellationToken)
                                .ConfigureAwait(false)
                        ];
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or HttpRequestException)
        {
            return TomixResult<DeployModelResult>.Fail(
                "TOMIX_BPA_RULES_LOAD_FAILED",
                ex.Message,
                exitCode: 2);
        }

        var snapshot = await session.GetSnapshotAsync(cancellationToken);
        var engine = new BpaEngine();
        var options = new BpaEngineOptions(rules, null, null);
        var result = engine.Evaluate(snapshot, options);

        if (result.Violations.Count == 0)
            return null;

        BpaRunResult? postFixResult = null;
        if (request.FixBpa)
        {
            if (session is not IModelMutationSession mutationSession)
                return TomixResult<DeployModelResult>.Fail(
                    "TOMIX_DEPLOY_FIX_UNSUPPORTED",
                    $"Provider cannot apply BPA fixes for model: {request.Model.Value}. Use --skip-bpa to bypass.",
                    exitCode: 2);

            var fixer = new BpaFixer();
            fixer.ApplyFixes(mutationSession, result.Violations, rules);

            // Re-evaluate so the gate reflects the actual post-fix state, catching both
            // unfixable violations and any fixes that did not resolve their target.
            var postFixSnapshot = await session.GetSnapshotAsync(cancellationToken);
            postFixResult = engine.Evaluate(postFixSnapshot, options);
        }

        return EvaluateBpaGate(result.Violations, postFixResult?.Violations, request.FixBpa);
    }

    /// <summary>
    /// Pure decision logic for the BPA deploy gate, extracted for branch-complete testing.
    /// Without <paramref name="fixBpa"/>: fail on any violation. With <paramref name="fixBpa"/>:
    /// fail only if any error-severity violation remains after fixes (warnings/info are tolerated).
    /// Returns <c>null</c> when the deploy may proceed.
    /// </summary>
    internal static TomixResult<DeployModelResult>? EvaluateBpaGate(
        IReadOnlyList<BpaViolation> violations,
        IReadOnlyList<BpaViolation>? postFixViolations,
        bool fixBpa)
    {
        if (violations.Count == 0)
            return null;

        if (fixBpa)
        {
            var remainingErrors = (postFixViolations ?? [])
                .Where(v => v.Severity == BpaSeverity.Error)
                .ToList();

            if (remainingErrors.Count == 0)
                return null;

            return TomixResult<DeployModelResult>.Fail(
                "TOMIX_BPA_VIOLATIONS",
                $"BPA check found {remainingErrors.Count} error-severity violation(s) remaining after auto-fix. Use --skip-bpa to bypass.",
                exitCode: 1);
        }

        return TomixResult<DeployModelResult>.Fail(
            "TOMIX_BPA_VIOLATIONS",
            $"BPA check found {violations.Count} violation(s). Use --fix-bpa to auto-fix or --skip-bpa to bypass.",
            exitCode: 1);
    }

    private static (string? server, string? database) ResolveTarget(
        DeployModelRequest request, CliStateStore store, Func<CliConnectionState?> resolveSession)
    {
        if (!string.IsNullOrWhiteSpace(request.Server))
            return (request.Server, request.Database);

        if (!string.IsNullOrWhiteSpace(request.Profile))
        {
            var profiles = store.LoadProfiles();
            if (profiles.TryGetValue(request.Profile, out var profile))
                return (profile.Server, profile.Database ?? request.Database);
        }

        var session = resolveSession();
        if (session is not null)
        {
            if (!string.IsNullOrWhiteSpace(session.Server))
                return (session.Server, session.Database ?? request.Database);

            // Local primary with a remote workspace-mode mirror: deploy targets the mirror,
            // matching refresh's primary-if-remote-else-secondary resolution.
            var mirror = ActiveModelResolver.ResolveSyncTarget(session);
            if (mirror is not null && mirror.IsRemote)
                return (mirror.Value, mirror.Database ?? request.Database);
        }

        return (null, request.Database);
    }
}
