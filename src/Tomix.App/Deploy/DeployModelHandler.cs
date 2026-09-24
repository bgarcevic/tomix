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
    private readonly BpaUserRuleState? _bpaRules;

    public DeployModelHandler(
        IEnumerable<IModelProvider> providers,
        CliStateStore state,
        Func<CliConnectionState?>? sessionOverride = null,
        HttpClient? httpClient = null,
        BpaUserRuleState? bpaRules = null)
    {
        _providers = providers.ToList();
        _state = state;
        _resolveSession = sessionOverride ?? state.LoadCurrentSession;
        _httpClient = httpClient;
        _bpaRules = bpaRules;
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

        // Validated even when --skip-bpa makes the threshold unused, so a typo fails as a usage
        // error instead of being silently ignored.
        if (!BpaFailOn.TryParse(request.BpaFailOn, "--bpa-fail-on", out var bpaFailOn, out var bpaFailOnError))
            return TomixResult<DeployModelResult>.Fail(
                "TOMIX_BPA_INVALID_FAIL_ON",
                bpaFailOnError!,
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
            var bpaResult = await RunBpaGate(session, request, bpaFailOn, cancellationToken);
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
            bool? createsDatabase = null;

            if (!string.IsNullOrWhiteSpace(server) && !string.IsNullOrWhiteSpace(database))
            {
                try
                {
                    // The plan reads the target once and returns both the target's current model
                    // and the model this deploy would leave behind under the same options a real
                    // deploy uses, so preserved objects never surface as changes.
                    var plan = await deployer.GeneratePlanAsync(deployRequest, cancellationToken);

                    if (!plan.TargetExists)
                        // Nothing to compare against: the deploy creates the database and ships
                        // the full source model.
                        createsDatabase = true;
                    else
                        // Target first: the dry run answers "what will this deploy change on the
                        // target", so added/removed/old→new read in the deploy's direction. The
                        // target is a processed database, so engine-computed state is ignored.
                        diff = DiffModelHandler.Diff(
                            plan.Target!, plan.Planned, ignoreEngineComputedState: true);
                }
                // Keep the reason: "not authenticated" and "target unreachable" are different
                // situations and the dry-run output should not conflate them.
                catch (AuthenticationRequiredException ex)
                {
                    diffError = ex.Message;
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not ModelLoadException)
                {
                    diffError = $"Cannot read target '{server}': {ex.InnerException?.Message ?? ex.Message}";
                }
            }

            return TomixResult<DeployModelResult>.Ok(new DeployModelResult(
                server, database ?? request.Model.Value, "dry-run", null, null, null, diff, diffError,
                createsDatabase));
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
        BpaSeverity failOn,
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
        // Issue #254: user-level disables (`bpa rules disable`) must reach the gate exactly as
        // they reach `bpa run`, so the two agree on the same machine. A handler built without
        // the state disables nothing; the path/rule filters stay empty — a gate evaluates all.
        var userDisabled = _bpaRules?.GetDisabled().ToList();
        var options = new BpaEngineOptions(rules, null, null, userDisabled);
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

        // postFixResult is always set when FixBpa reaches this line (unsupported providers fail
        // above), so the active phase — the one the blocking count comes from — supplies the
        // unevaluable-rule findings named in the block message.
        var activeResult = request.FixBpa ? postFixResult : result;
        return EvaluateBpaGate(result.Violations, postFixResult?.Violations, request.FixBpa, failOn,
            ruleErrors: activeResult!.RuleErrorViolations);
    }

    /// <summary>
    /// Pure decision logic for the BPA deploy gate, extracted for branch-complete testing.
    /// Both phases apply the same severity threshold: the deploy is blocked only when a
    /// violation at or above <paramref name="failOn"/> remains — among the pre-fix violations
    /// when <paramref name="fixBpa"/> is false, otherwise among the post-fix re-evaluation
    /// (findings below the threshold are tolerated). Returns <c>null</c> when the deploy may
    /// proceed. <paramref name="ruleErrors"/> is the active phase's unevaluable-rule findings
    /// (already part of <paramref name="violations"/>); naming them in the message makes the
    /// block actionable instead of an anonymous count.
    /// </summary>
    internal static TomixResult<DeployModelResult>? EvaluateBpaGate(
        IReadOnlyList<BpaViolation> violations,
        IReadOnlyList<BpaViolation>? postFixViolations,
        bool fixBpa,
        BpaSeverity failOn,
        IReadOnlyList<BpaViolation>? ruleErrors = null)
    {
        if (violations.Count == 0)
            return null;

        var blocking = BpaFailOn.Blocking(fixBpa ? postFixViolations ?? [] : violations, failOn);
        if (blocking.Count == 0)
            return null;

        var severityLabel = failOn == BpaSeverity.Warning ? "warning-severity or higher" : "error-severity";
        var phase = fixBpa ? " remaining after auto-fix" : string.Empty;
        var hint = fixBpa
            ? "Use --skip-bpa to bypass."
            : "Use --fix-bpa to auto-fix or --skip-bpa to bypass.";
        var ruleErrorNotes = ruleErrors is { Count: > 0 }
            ? " " + string.Join(" ", ruleErrors.Select(e => $"{e.Description} ('{e.RuleName}' [{e.RuleId}])."))
            : string.Empty;

        return TomixResult<DeployModelResult>.Fail(
            "TOMIX_BPA_VIOLATIONS",
            $"BPA check found {blocking.Count} {severityLabel} violation(s){phase}.{ruleErrorNotes} {hint}",
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
