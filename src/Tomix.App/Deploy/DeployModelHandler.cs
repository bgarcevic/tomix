using Tomix.App.Bpa;
using Tomix.App.Diff;
using Tomix.App.Models;
using Tomix.App.State;
using Tomix.Core.Authentication;
using Tomix.Core.Bpa;
using Tomix.Core.Diagnostics;
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

    /// <summary>Previews (<see cref="DeployModelRequest.Preview"/>) or runs one deploy.</summary>
    public async Task<TomixResult<DeployModelResult>> HandleAsync(
        DeployModelRequest request,
        CancellationToken cancellationToken)
    {
        await using var operation = await OpenAsync(request, cancellationToken);
        return request.Preview
            ? await operation.PreviewAsync(cancellationToken)
            : await operation.ApplyAsync(cancellationToken);
    }

    /// <summary>
    /// Validates the request, opens the source model, and runs the BPA gate once. The returned
    /// operation previews and applies against that same session, so a preview-then-confirm flow
    /// opens the model and evaluates the gate only once. A failure here is carried by the
    /// operation and returned by whichever of its methods is called.
    /// </summary>
    public async Task<DeployOperation> OpenAsync(
        DeployModelRequest request,
        CancellationToken cancellationToken)
    {
        CliProfile? profile = null;
        if (request.Profile is not null)
        {
            var resolved = DeployProfileResolver.Resolve(_state, request.Profile);
            if (!resolved.Success)
            {
                var diagnostic = resolved.Diagnostics[0];
                return DeployOperation.Failed(TomixResult<DeployModelResult>.Fail(
                    diagnostic.Code, diagnostic.Message, resolved.ExitCode, diagnostic.Hint));
            }

            profile = resolved.Data!;
        }

        var deployOptions = request.DeployOptions ?? ModelDeployOptions.Preserve;
        if (deployOptions.DeployRoleMembers && !deployOptions.DeployRoles)
            return DeployOperation.Failed(TomixResult<DeployModelResult>.Fail(
                "TOMIX_DEPLOY_INVALID_FLAGS",
                "--deploy-role-members requires --deploy-roles: members cannot be overwritten while the target's role definitions are preserved.",
                exitCode: 2));

        if (deployOptions.DeployPolicyPartitions && !deployOptions.DeployPartitions)
            return DeployOperation.Failed(TomixResult<DeployModelResult>.Fail(
                "TOMIX_DEPLOY_INVALID_FLAGS",
                "--deploy-policy-partitions requires --deploy-partitions.",
                exitCode: 2));

        // Validated even when --skip-bpa makes the threshold unused, so a typo fails as a usage
        // error instead of being silently ignored.
        if (!BpaFailOn.TryParse(request.BpaFailOn, "--bpa-fail-on", out var bpaFailOn, out var bpaFailOnError))
            return DeployOperation.Failed(TomixResult<DeployModelResult>.Fail(
                "TOMIX_BPA_INVALID_FAIL_ON",
                bpaFailOnError!,
                exitCode: 2));

        if (request.Model.Value.Length == 0)
            return DeployOperation.Failed(TomixResult<DeployModelResult>.Fail(
                "TOMIX_NO_MODEL",
                "No model specified. Use --model <path>, --server <url> --database <name>, or set an active connection with 'tx connect'.",
                exitCode: 2,
                hint: "Specify a model path or use --recent."));

        var provider = _providers.ResolveSingleProvider(request.Model);
        if (provider is null)
            return DeployOperation.Failed(TomixResult<DeployModelResult>.Fail(
                "TOMIX_NO_PROVIDER",
                $"No provider can open model: {request.Model.Value}",
                exitCode: 2,
                hint: "Supported formats: TMDL folder, .bim file. For remote models, use --server and --database."));

        var session = await provider.OpenAsync(request.Model, cancellationToken);
        try
        {

            // Non-fatal gate findings (rules the gate could not check) ride along on every
            // successful outcome, so a deploy never looks fully gated when it was not.
            IReadOnlyList<TomixDiagnostic> warnings = [];
            if (!request.SkipBpa)
            {
                var (bpaResult, notChecked) = await RunBpaGate(session, request, bpaFailOn, cancellationToken);
                if (bpaResult is not null)
                {
                    await session.DisposeAsync();
                    return DeployOperation.Failed(bpaResult);
                }
                warnings = MissingVertipaqStatsWarning(notChecked);
            }

            if (session is not IModelDeploySession deployer)
            {
                await session.DisposeAsync();
                return DeployOperation.Failed(TomixResult<DeployModelResult>.Fail(
                    "TOMIX_DEPLOY_UNSUPPORTED",
                    $"Provider cannot deploy model: {request.Model.Value}",
                    exitCode: 1));
            }

            var (server, database) = ResolveTarget(request, profile, _resolveSession);

            if (string.IsNullOrWhiteSpace(server))
            {
                await session.DisposeAsync();
                return DeployOperation.Failed(TomixResult<DeployModelResult>.Fail(
                    "TOMIX_DEPLOY_NO_TARGET",
                    "No target workspace specified. Use -s/--server or set an active connection with 'tx connect'.",
                    exitCode: 2,
                    hint: "Specify --workspace or --server and --database."));
            }

            var deployRequest = new ModelDeployRequest(
                server,
                database,
                request.CreateOnly,
                Force: false,
                deployOptions);

            return new DeployOperation(session, deployer, deployRequest, request, server, database, warnings);
        }
        catch
        {
            // The operation owns the session only once it is returned.
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The deploy-side face of #266: rules that read VertiPaq statistics the model does not
    /// have are skipped, not passed. Warn by default — missing statistics are the normal state
    /// of a fresh model — but name the rules and the command that collects the statistics.
    /// </summary>
    internal static IReadOnlyList<TomixDiagnostic> MissingVertipaqStatsWarning(IReadOnlyList<BpaResult> notChecked)
        => notChecked.Count == 0
            ? []
            :
            [
                new TomixDiagnostic(
                    "TOMIX_BPA_VERTIPAQ_STATS_MISSING",
                    DiagnosticSeverity.Warning,
                    $"BPA gate skipped {notChecked.Count} rule(s) that need VertiPaq statistics: "
                        + string.Join(", ", notChecked.Select(r => r.RuleId)) + ".",
                    // A plain model file cannot collect statistics itself; it reads them from a
                    // deployed copy through workspace mode.
                    Hint: $"Run '{BpaEngine.VertipaqAnnotateCommand}' on a deployed model. For a model file, "
                        + $"connect it to a deployed copy in workspace mode first ({BpaEngine.VertipaqWorkspaceConnectCommand}).")
            ];

    private async Task<(TomixResult<DeployModelResult>? Failure, IReadOnlyList<BpaResult> NotChecked)> RunBpaGate(
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
            return (TomixResult<DeployModelResult>.Fail(
                "TOMIX_BPA_RULES_LOAD_FAILED",
                ex.Message,
                exitCode: 2), []);
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
            return (null, result.MissingVertipaqStatsRules);

        BpaRunResult? postFixResult = null;
        if (request.FixBpa)
        {
            if (session is not IModelMutationSession mutationSession)
                return (TomixResult<DeployModelResult>.Fail(
                    "TOMIX_DEPLOY_FIX_UNSUPPORTED",
                    $"Provider cannot apply BPA fixes for model: {request.Model.Value}. Use --skip-bpa to bypass.",
                    exitCode: 2), []);

            var fixer = new BpaFixer();
            fixer.ApplyFixes(mutationSession, result.Violations, rules, snapshot: snapshot);

            // Re-evaluate so the gate reflects the actual post-fix state, catching both
            // unfixable violations and any fixes that did not resolve their target.
            var postFixSnapshot = await session.GetSnapshotAsync(cancellationToken);
            postFixResult = engine.Evaluate(postFixSnapshot, options);
        }

        // postFixResult is always set when FixBpa reaches this line (unsupported providers fail
        // above), so the active phase — the one the blocking count comes from — supplies the
        // unevaluable-rule findings named in the block message.
        var activeResult = request.FixBpa ? postFixResult : result;
        var failure = EvaluateBpaGate(result.Violations, postFixResult?.Violations, request.FixBpa, failOn,
            ruleErrors: activeResult!.RuleErrorViolations,
            notChecked: activeResult.MissingVertipaqStatsRules);
        return (failure, activeResult.MissingVertipaqStatsRules);
    }

    /// <summary>
    /// Pure decision logic for the BPA deploy gate, extracted for branch-complete testing.
    /// Both phases apply the same severity threshold: the deploy is blocked only when a
    /// violation at or above <paramref name="failOn"/> remains — among the pre-fix violations
    /// when <paramref name="fixBpa"/> is false, otherwise among the post-fix re-evaluation
    /// (findings below the threshold are tolerated). Returns <c>null</c> when the deploy may
    /// proceed. <paramref name="ruleErrors"/> is the active phase's unevaluable-rule findings
    /// (already part of <paramref name="violations"/>); naming them in the message makes the
    /// block actionable instead of an anonymous count. <paramref name="notChecked"/> names the
    /// rules skipped for missing VertiPaq statistics, so a block never implies they passed.
    /// </summary>
    internal static TomixResult<DeployModelResult>? EvaluateBpaGate(
        IReadOnlyList<BpaViolation> violations,
        IReadOnlyList<BpaViolation>? postFixViolations,
        bool fixBpa,
        BpaSeverity failOn,
        IReadOnlyList<BpaViolation>? ruleErrors = null,
        IReadOnlyList<BpaResult>? notChecked = null)
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
        var notCheckedNote = notChecked is { Count: > 0 }
            ? $" Not checked (no VertiPaq statistics): {string.Join(", ", notChecked.Select(r => r.RuleId))}."
            : string.Empty;

        return TomixResult<DeployModelResult>.Fail(
            "TOMIX_BPA_VIOLATIONS",
            $"BPA check found {blocking.Count} {severityLabel} violation(s){phase}.{ruleErrorNotes}{notCheckedNote} {hint}",
            exitCode: 1);
    }

    private static (string? server, string? database) ResolveTarget(
        DeployModelRequest request, CliProfile? profile, Func<CliConnectionState?> resolveSession)
    {
        if (!string.IsNullOrWhiteSpace(request.Server))
            return (request.Server, request.Database);

        if (profile is not null)
            return (profile.Server, profile.Database ?? request.Database);

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
