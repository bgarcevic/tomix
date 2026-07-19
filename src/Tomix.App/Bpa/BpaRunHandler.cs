using System.Text.Json;
using Tomix.App.Diagnostics;
using Tomix.App.Mutations;
using Tomix.App.State;
using Tomix.Core.Bpa;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Bpa;

public sealed class BpaRunHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly MutationStores _stores;
    private readonly BpaUserRuleState _userRules;
    private readonly string _configDirectory;

    public BpaRunHandler(
        IEnumerable<IModelProvider> providers,
        MutationStores stores,
        BpaUserRuleState userRules,
        string configDirectory)
    {
        _providers = providers.ToList();
        _stores = stores;
        _userRules = userRules;
        _configDirectory = configDirectory;
    }

    public async Task<TomixResult<BpaRunResult>> HandleAsync(
        BpaRunRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryParseFailOn(request.FailOn, out var failOnSeverity, out var failOnError))
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
            request.NoSync);
        var stagingStore = _stores.Staging;
        var connection = _stores.ResolveSession();

        var begin = await MutationLifecycle.BeginAsync(
            _providers, request.Model, options, stagingStore, connection, cancellationToken);
        if (begin.Error is { } error)
            return TomixResult<BpaRunResult>.Fail(error.Code, error.Message, error.ExitCode);

        // The staging handle holds the per-model lock; release it on every exit path.
        using var stagingHandle = begin.Context?.Staging;

        if (begin.Mode == MutationMode.Revert)
        {
            stagingStore.Discard(request.Model);
            return TomixResult<BpaRunResult>.Ok(new BpaRunResult([], "", 0));
        }

        var context = begin.Context!;
        var provider = _providers.ResolveSingle(context.EffectiveModel);
        if (provider is null)
            return TomixResult<BpaRunResult>.Fail(
                "TOMIX_NO_PROVIDER",
                $"No provider can open model: {context.EffectiveModel.Value}",
                exitCode: 2,
                hint: "Supported formats: TMDL folder, .bim file. For remote models, use --server and --database.");

        return await ProviderConnectionGuard.RunAsync(request.Model, async () =>
        {
            await using var session = await provider.OpenAsync(context.EffectiveModel, cancellationToken);
            var snapshot = await session.GetSnapshotAsync(cancellationToken);

            IReadOnlyList<BpaRule> rules;
            IReadOnlyList<string> loadDiagnostics;
            try
            {
                (rules, loadDiagnostics) = await LoadRulesAsync(request, snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or HttpRequestException or JsonException)
            {
                return TomixResult<BpaRunResult>.Fail(
                    "TOMIX_BPA_RULES_LOAD_FAILED",
                    ex.Message,
                    exitCode: 2);
            }

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
                RuleLoadDiagnostics = loadDiagnostics.Count > 0 ? loadDiagnostics : null
            };

            if (request.Fix && runResult.Violations.Any(v => v.CanFix))
            {
                if (session is not IModelMutationSession mutationSession)
                    return TomixResult<BpaRunResult>.Fail(
                        "TOMIX_MUTATION_UNSUPPORTED_PROVIDER",
                        $"Provider cannot mutate model: {context.EffectiveModel.Value}");

                var fixer = new BpaFixer();
                var fixResult = fixer.ApplyFixes(mutationSession, runResult.Violations, rules, request.AllowDelete);

                runResult = runResult with
                {
                    FixesApplied = fixResult.FixesApplied,
                    FixesSkipped = fixResult.FixesSkipped,
                    DestructiveFixesSkipped = fixResult.DestructiveFixesSkipped,
                    FixErrors = fixResult.Errors.Count > 0
                        ? fixResult.Errors.Select(e => $"[{e.RuleId}] {e.ObjectPath}: {e.Reason}").ToList()
                        : null
                };

                if (fixResult.FixesApplied > 0 && context.Mode is MutationMode.Save or MutationMode.Stage)
                {
                    var outcome = await MutationLifecycle.CompleteAsync(
                        mutationSession, context, "bpa-fix",
                        $"bpa-fix {fixResult.FixesApplied} violations", cancellationToken);

                    runResult = runResult with
                    {
                        Saved = outcome.Saved,
                        Staged = outcome.Staged,
                        Synced = outcome.Synced,
                        SyncTarget = outcome.SyncTarget,
                        SyncWarning = outcome.SyncWarning
                    };
                }
            }

            return TomixResult<BpaRunResult>.Ok(runResult, exitCode: ShouldFail(runResult, failOnSeverity) ? 1 : 0);
        });
    }

    private async Task<(IReadOnlyList<BpaRule> Rules, IReadOnlyList<string> Diagnostics)> LoadRulesAsync(
        BpaRunRequest request,
        ModelSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var collections = new List<BpaRuleCollection>();
        var diagnostics = new List<string>();

        if (!request.NoDefaults)
        {
            var label = string.IsNullOrWhiteSpace(request.Ruleset) ? BpaRuleLoader.StandardRuleset : request.Ruleset;
            collections.Add(new BpaRuleCollection(
                BpaRuleSourceKind.Machine, label,
                await BpaRuleLoader.LoadRulesetAsync(request.Ruleset, cancellationToken).ConfigureAwait(false)));
        }

        if (request.RulesFiles is not null)
        {
            foreach (var file in request.RulesFiles)
            {
                if (!string.IsNullOrWhiteSpace(file))
                    collections.Add(new BpaRuleCollection(
                        BpaRuleSourceKind.User, file,
                        await BpaRuleLoader.LoadFromSourceAsync(file, cancellationToken).ConfigureAwait(false)));
            }
        }

        var userRulesPath = Path.Combine(_configDirectory, "bpa-rules.json");
        if (File.Exists(userRulesPath))
        {
            var userRules = BpaRuleLoader.LoadFromFile(userRulesPath);
            if (userRules.Count > 0)
                collections.Add(new BpaRuleCollection(BpaRuleSourceKind.User, userRulesPath, userRules));
        }

        if (!request.NoModelRules)
        {
            var model = await BpaModelRuleLoader.LoadAsync(
                snapshot.Properties,
                ModelBaseDirectory(request.Model),
                request.AllowExternalRules,
                cancellationToken).ConfigureAwait(false);

            collections.AddRange(model.Collections);
            diagnostics.AddRange(model.Diagnostics);
        }

        return (BpaRuleResolver.Resolve(collections), diagnostics);
    }

    private static string? ModelBaseDirectory(ModelReference model)
    {
        if (!model.IsLocalPath)
            return null;

        try
        {
            return Directory.Exists(model.Value)
                ? model.Value
                : Path.GetDirectoryName(Path.GetFullPath(model.Value));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool TryParseFailOn(
        string? value,
        out BpaSeverity severity,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            severity = BpaSeverity.Error;
            error = null;
            return true;
        }

        if (value.Equals("warning", StringComparison.OrdinalIgnoreCase))
        {
            severity = BpaSeverity.Warning;
            error = null;
            return true;
        }

        severity = BpaSeverity.Error;
        error = $"Invalid --fail-on value '{value}'. Expected: error or warning.";
        return false;
    }

    private static bool ShouldFail(BpaRunResult result, BpaSeverity threshold)
        => threshold switch
        {
            BpaSeverity.Error => result.Violations.Any(v => v.Severity == BpaSeverity.Error),
            BpaSeverity.Warning => result.Violations.Any(v => v.Severity is BpaSeverity.Warning or BpaSeverity.Error),
            _ => result.Violations.Count > 0
        };
}
