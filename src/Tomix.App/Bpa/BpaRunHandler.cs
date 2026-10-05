using System.Text.Json;
using Tomix.App.Diagnostics;
using Tomix.App.Models;
using Tomix.App.Mutations;
using Tomix.App.State;
using Tomix.Core.Bpa;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Bpa;

public sealed class BpaRunHandler
{
    private readonly IModelSessionSource _sessions;
    private readonly MutationStores _stores;
    private readonly BpaUserRuleState _userRules;
    private readonly string _configDirectory;
    private readonly HttpClient? _httpClient;
    private readonly Func<string, string?> _environment;

    public BpaRunHandler(
        IEnumerable<IModelProvider> providers,
        MutationStores stores,
        BpaUserRuleState userRules,
        string configDirectory,
        HttpClient? httpClient = null,
        Func<string, string?>? environment = null)
        : this(new OneShotSessionSource(providers), stores, userRules, configDirectory, httpClient, environment)
    {
    }

    public BpaRunHandler(
        IModelSessionSource sessions,
        MutationStores stores,
        BpaUserRuleState userRules,
        string configDirectory,
        HttpClient? httpClient = null,
        Func<string, string?>? environment = null)
    {
        _sessions = sessions;
        _stores = stores;
        _userRules = userRules;
        _configDirectory = configDirectory;
        _httpClient = httpClient;
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    public async Task<TomixResult<BpaRunResult>> HandleAsync(
        BpaRunRequest request,
        CancellationToken cancellationToken)
    {
        if (!BpaFailOn.TryParse(request.FailOn, "--fail-on", out var failOnSeverity, out var failOnError))
            return TomixResult<BpaRunResult>.Fail(
                "TOMIX_BPA_INVALID_FAIL_ON",
                failOnError!,
                exitCode: 2);

        var options = new MutationOptions(
            request.Save && request.Fix,
            request.SaveTo,
            request.Stage && request.Fix,
            request.Revert,
            request.Serialization,
            request.Force,
            request.Overwrite,
            request.NoSync);
        var stagingStore = _stores.Staging;
        var connection = _stores.ResolveSession();

        var begin = await MutationLifecycle.BeginAsync(
            _sessions, request.Model, options, stagingStore, connection, cancellationToken);
        if (begin.Error is { } error)
            return TomixResult<BpaRunResult>.Fail(error.Code, error.Message, error.ExitCode);

        // The staging handle holds the per-model lock; release it on every exit path.
        using var stagingHandle = begin.Context?.Staging;

        if (begin.Mode == MutationMode.Revert)
        {
            stagingStore.Discard(request.Model);
            return TomixResult<BpaRunResult>.Ok(new BpaRunResult([], "", 0) { FixOutcome = MutationOutcome.Reverted });
        }

        var context = begin.Context!;
        return await ProviderConnectionGuard.RunAsync(request.Model, async () =>
        {
            ModelSessionLease lease;
            try
            {
                lease = await _sessions.LeaseAsync(context.EffectiveModel, cancellationToken);
            }
            catch (ModelSessionUnavailableException ex)
            {
                return ModelSessionRunner.Unavailable<BpaRunResult>(ex);
            }

            // Ending the lease without a commit rolls a live session's fixes back.
            await using var _ = lease;
            var result = await RunAsync(lease.Session);
            if (result.Success)
                await lease.CommitAsync(cancellationToken);
            return result;
        });

        async Task<TomixResult<BpaRunResult>> RunAsync(IModelSession session)
        {
            var snapshot = await session.GetSnapshotAsync(cancellationToken);
            var validationBaseline = SaveValidation.ForSnapshot(
                snapshot, context, _stores.ShouldValidateOnSave());

            // A staged run analyzes a working copy under the config directory, but the annotation's
            // relative external-rule paths are anchored at the original model, so resolve from that
            // instead of the copy.
            var ruleBaseDirectory = context.Staging is null
                ? BpaModelRuleLoader.ResolveBaseDirectory(session, request.Model)
                : await ResolveStagedRuleBaseDirectoryAsync(request.Model, cancellationToken);

            BpaResolvedRules resolved;
            IReadOnlyList<string> loadDiagnostics;
            try
            {
                (resolved, loadDiagnostics) = await LoadRulesAsync(request, snapshot, ruleBaseDirectory, cancellationToken).ConfigureAwait(false);
            }
            // InvalidOperationException: a corrupt config file, which carries the bpa.rules key.
            catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or HttpRequestException or JsonException or InvalidOperationException)
            {
                return TomixResult<BpaRunResult>.Fail(
                    "TOMIX_BPA_RULES_LOAD_FAILED",
                    ex.Message,
                    exitCode: 2);
            }

            var rules = resolved.Rules;
            var userDisabled = _userRules.GetDisabled().ToList();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var engine = new BpaEngine();
            var result = engine.Evaluate(snapshot, new BpaEngineOptions(
                rules,
                request.PathFilter,
                request.RuleIds,
                userDisabled));
            sw.Stop();

            var runResult = result with
            {
                DurationMs = sw.ElapsedMilliseconds,
                RuleSources = resolved.Sources,
                RuleLoadDiagnostics = loadDiagnostics.Count > 0 ? loadDiagnostics : null
            };

            if (request.Fix && runResult.Violations.Any(v => v.CanFix))
            {
                if (session is not IModelMutationSession mutationSession)
                    return TomixResult<BpaRunResult>.Fail(
                        "TOMIX_MUTATION_UNSUPPORTED_PROVIDER",
                        $"Provider cannot mutate model: {context.EffectiveModel.Value}");

                var fixer = new BpaFixer();
                // Without --save/--stage the run is a preview: the fixes are applied to the
                // in-memory model too, so it shows the values the provider would really write and
                // what would remain, and the lifecycle then discards them.
                var preview = !context.KeepsEdit;
                var fixResult = fixer.ApplyFixes(mutationSession, runResult.Violations, rules, request.AllowDelete, snapshot);

                runResult = runResult with
                {
                    FixesApplied = preview ? 0 : fixResult.FixesApplied,
                    FixChanges = BpaFixer.WithBefore(fixResult.Changes, snapshot),
                    Preview = preview,
                    FixesSkipped = fixResult.FixesSkipped,
                    DestructiveFixesSkipped = fixResult.DestructiveFixesSkipped,
                    FixErrors = fixResult.Errors.Count > 0
                        ? fixResult.Errors.Select(e => $"[{e.RuleId}] {e.ObjectPath}: {e.Reason}").ToList()
                        : null
                };

                // Judge the exit code on what the fixes left behind, as the deploy gate does:
                // re-evaluate the mutated model so unfixable findings and fixes that did not
                // resolve their target still block (#297).
                if (fixResult.FixesApplied > 0)
                {
                    var postFixSnapshot = await session.GetSnapshotAsync(cancellationToken);
                    var postFix = engine.Evaluate(postFixSnapshot, new BpaEngineOptions(
                        rules,
                        request.PathFilter,
                        request.RuleIds,
                        userDisabled));
                    runResult = preview
                        ? runResult with { ProjectedViolations = postFix.Violations }
                        : runResult with { RemainingViolations = postFix.Violations };
                }

                if (preview)
                    runResult = runResult with { FixOutcome = MutationOutcome.Preview };

                if (fixResult.FixesApplied > 0 && context.KeepsEdit)
                {
                    MutationOutcome outcome;
                    try
                    {
                        outcome = await MutationLifecycle.CompleteAsync(
                            mutationSession, session, context, validationBaseline, "bpa-fix",
                            $"bpa-fix {fixResult.FixesApplied} violations", cancellationToken);
                    }
                    catch (SaveValidationBlockedException ex)
                    {
                        return SaveValidation.Blocked<BpaRunResult>(ex.Delta);
                    }

                    runResult = runResult with { FixOutcome = outcome with { Target = MutationTarget.Merge(MutationTarget.For(request.Model, connection), outcome.Target) } };
                    if (context.Force && outcome.Validation is { NewErrorCount: > 0 } delta)
                        return TomixResult<BpaRunResult>.Ok(
                            runResult,
                            exitCode: BpaFailOn.Blocking(runResult.BlockingCandidates, failOnSeverity).Count > 0 ? 1 : 0,
                            diagnostics: SaveValidation.ForcedNotice(delta));
                }
            }

            return TomixResult<BpaRunResult>.Ok(runResult, exitCode: BpaFailOn.Blocking(runResult.BlockingCandidates, failOnSeverity).Count > 0 ? 1 : 0);
        }
    }

    /// <summary>
    /// The folder a staged run's relative external-rule paths resolve against: the original model's
    /// own source folder, which for a .pbip/.pbism/project-root reference is the nested definition
    /// folder rather than the reference itself. Opening a local model only resolves paths (the
    /// database is deserialized lazily), so probing it here stays cheap.
    /// </summary>
    private async Task<string?> ResolveStagedRuleBaseDirectoryAsync(
        ModelReference model,
        CancellationToken cancellationToken)
    {
        if (model.IsLocalPath)
        {
            try
            {
                // Provider matching itself touches the filesystem — resolving a .pbip reference
                // reads the file and enumerates sibling folders — so it stays inside the guard:
                // this is a best-effort probe and an unreadable original must not fail the run.
                // Only a one-shot source stages, so only it has providers to probe with.
                if (_sessions is OneShotSessionSource { Providers: var providers }
                    && providers.ResolveSingleProvider(model) is { } provider)
                {
                    await using var probe = await provider.OpenAsync(model, cancellationToken);
                    return BpaModelRuleLoader.ResolveBaseDirectory(probe, model);
                }
            }
            catch (Exception ex) when (ex
                is IOException or NotSupportedException or UnauthorizedAccessException
                or AmbiguousModelProviderException or ModelLoadException)
            {
                // The original may be gone, unreadable (ResolveSingleProvider reports that as
                // ModelLoadException), or ambiguously claimed; fall back below.
            }
        }

        return BpaModelRuleLoader.ResolveBaseDirectory(session: null, model);
    }

    private async Task<(BpaResolvedRules Resolved, IReadOnlyList<string> Diagnostics)> LoadRulesAsync(
        BpaRunRequest request,
        ModelSnapshot snapshot,
        string? modelRuleBaseDirectory,
        CancellationToken cancellationToken)
    {
        var collections = new List<BpaRuleCollection>();
        var diagnostics = new List<string>();

        if (!request.NoDefaults)
        {
            foreach (var preset in BpaRuleLoader.SplitRulesets(request.Ruleset))
                collections.Add(new BpaRuleCollection(
                    BpaRuleSourceKind.Machine, preset,
                    await BpaRuleLoader
                        .LoadRulesetAsync(preset, _httpClient, cancellationToken)
                        .ConfigureAwait(false),
                    BpaRuleOrigin.Ruleset));
        }

        // The rule-source chain (#233), lowest precedence first: all links are user rules, so a
        // later link overrides an earlier one for the same id.
        var userRulesPath = Path.Combine(_configDirectory, "bpa-rules.json");
        if (File.Exists(userRulesPath))
        {
            var userRules = BpaRuleLoader.LoadFromFile(userRulesPath);
            if (userRules.Count > 0)
                collections.Add(new BpaRuleCollection(BpaRuleSourceKind.User, userRulesPath, userRules, BpaRuleOrigin.UserFile));
        }

        foreach (var entry in BpaRuleSources.Resolve(_configDirectory, _environment, request.RulesFiles))
            collections.Add(new BpaRuleCollection(
                BpaRuleSourceKind.User, entry.Location,
                await BpaRuleSources.LoadEntryAsync(entry, _httpClient, cancellationToken).ConfigureAwait(false),
                entry.Origin));

        if (!request.NoModelRules)
        {
            var model = await BpaModelRuleLoader.LoadAsync(
                snapshot.Properties,
                modelRuleBaseDirectory,
                request.AllowExternalRules,
                BpaRuleHintContext.Run,
                _httpClient,
                cancellationToken).ConfigureAwait(false);

            collections.AddRange(model.Collections);
            diagnostics.AddRange(model.Diagnostics);
        }

        return (BpaRuleResolver.ResolveWithSources(collections), diagnostics);
    }

}
