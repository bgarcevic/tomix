using Tomix.App.Bpa;
using Tomix.App.Models;
using Tomix.App.Mutations;
using Tomix.Core.Bpa;
using Tomix.Core.Models;
using Tomix.Core.Results;
using Tomix.Core.Rules;

namespace Tomix.App.Save;

public sealed class SaveModelHandler
{
    private readonly IModelSessionSource _sessions;
    private readonly HttpClient? _httpClient;

    public SaveModelHandler(IEnumerable<IModelProvider> providers, HttpClient? httpClient = null)
        : this(new OneShotSessionSource(providers), httpClient)
    {
    }

    /// <summary>
    /// Saves the model <paramref name="sessions"/> leases. In a live session, writing back to the
    /// source makes the current state the save point, and <c>--fix-bpa</c> fixes become one undo step.
    /// </summary>
    public SaveModelHandler(IModelSessionSource sessions, HttpClient? httpClient = null)
    {
        _sessions = sessions;
        _httpClient = httpClient;
    }

    public Task<TomixResult<SaveModelResult>> HandleAsync(
        SaveModelRequest request,
        CancellationToken cancellationToken)
        => ModelSessionRunner.RunAsync(
            _sessions, request.Model, session => SaveAsync(session, request, cancellationToken), cancellationToken);

    private async Task<TomixResult<SaveModelResult>> SaveAsync(
        IModelSession session,
        SaveModelRequest request,
        CancellationToken cancellationToken)
    {
        var serialization = string.IsNullOrWhiteSpace(request.Serialization)
            ? InferSerialization(request.Model.Value)
            : request.Serialization;

        // A live session saves in place through its own save, which records the save point and
        // refuses to overwrite changes made outside it. Naming its own files is saving in place.
        var inPlace = string.IsNullOrWhiteSpace(request.OutputPath)
            || (_sessions.IsLive && SamePath(request.OutputPath, session.SourcePath));
        var outputPath = request.OutputPath;
        if (string.IsNullOrWhiteSpace(outputPath))
            outputPath = session.SourcePath;

        // A live session on a server saves to the server (#351); -o only writes a copy.
        var toServer = _sessions.IsLive && inPlace && request.Model.IsRemote;
        if (string.IsNullOrWhiteSpace(outputPath) && !toServer)
            return TomixResult<SaveModelResult>.Fail(
                code: "TOMIX_SAVE_OUTPUT_REQUIRED",
                message: "An output path is required when no model source is active.",
                exitCode: 2);

        if (request.FixBpa)
        {
            if (session is not IModelMutationSession mutationSession)
                return TomixResult<SaveModelResult>.Fail(
                    code: "TOMIX_SAVE_FIX_UNSUPPORTED",
                    message: "The model provider does not support applying BPA fixes.",
                    exitCode: 2);

            var bpaResult = await ApplyBpaFixes(session, mutationSession, request, cancellationToken);
            if (bpaResult is not null)
                return bpaResult;
        }

        if (session is not IModelExportSession exporter)
            return TomixResult<SaveModelResult>.Fail(
                code: "TOMIX_SAVE_UNSUPPORTED_PROVIDER",
                message: $"Provider cannot save model: {request.Model.Value}",
                exitCode: 1);

        try
        {
            // Keep the session's model over changes made to the files outside it (#351).
            if (request.Force && _sessions.IsLive && inPlace && session is IExternalChangeSession external)
                external.KeepChanges();

            var export = _sessions.IsLive && inPlace && session is IModelMutationSession live
                ? await live.SaveAsync(null, serialization, overwrite: true, cancellationToken)
                : await exporter.ExportAsync(
                    new ModelExportRequest(outputPath, serialization, request.Overwrite, request.SupportingFiles),
                    cancellationToken);

            var sync = await WorkspaceSync.SyncAsync(
                session, request.SyncTarget, request.Overwrite,
                // 'save' never edits a refresh policy, so the mirror's policy partitions (and their
                // processed data) are preserved — see WorkspaceSync.SyncOptionsFor.
                WorkspaceSync.SyncOptionsFor("save"), cancellationToken);

            // A failed workspace sync leaves the mirror behind the source; render the saved
            // result but exit non-zero so CI catches the drift.
            var (savedTo, persistence) = MutationLifecycle.Describe(request.Model, toServer ? null : outputPath, export.SavedPath);
            var outcome = new MutationOutcome(MutationStatus.Saved, savedTo, persistence, sync);
            return TomixResult<SaveModelResult>.Ok(
                new SaveModelResult(export.Format) { Outcome = outcome },
                outcome.SyncFailed ? 1 : 0);
        }
        catch (ModelSourceChangedException ex)
        {
            return Session.SourceChangedFailure.Result<SaveModelResult>(ex);
        }
        catch (NotSupportedException ex)
        {
            return TomixResult<SaveModelResult>.Fail("TOMIX_SAVE_UNSUPPORTED_SERIALIZATION", ex.Message, exitCode: 2);
        }
        catch (IOException ex)
        {
            return TomixResult<SaveModelResult>.Fail("TOMIX_SAVE_OUTPUT_EXISTS", ex.Message, exitCode: 2);
        }
    }

    private async Task<TomixResult<SaveModelResult>?> ApplyBpaFixes(
        IModelSession session,
        IModelMutationSession mutationSession,
        SaveModelRequest request,
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
            return TomixResult<SaveModelResult>.Fail("TOMIX_BPA_RULES_LOAD_FAILED", ex.Message, exitCode: 2);
        }

        var snapshot = await session.GetSnapshotAsync(cancellationToken);
        var engine = new BpaEngine();
        var result = engine.Evaluate(snapshot, new BpaEngineOptions(rules, null, null));

        if (result.Violations.Count == 0)
            return null;

        var fixer = new BpaFixer();
        var fixResult = fixer.ApplyFixes(mutationSession, result.Violations, rules, snapshot: snapshot);

        // Saving never remediates a rule that cannot be evaluated, so remaining rule errors
        // block regardless of other applied fixes (issue #253). Other error-severity findings
        // block only when nothing could be auto-fixed.
        if (result.RuleErrorViolations.Count > 0 ||
            (fixResult.FixesApplied == 0 && result.Violations.Any(v => v.Severity == RuleSeverity.Error)))
        {
            var ruleErrorNotes = result.RuleErrorViolations is { Count: > 0 }
                ? " " + string.Join(" ", result.RuleErrorViolations.Select(e => $"{e.Description} ('{e.RuleName}' [{e.RuleId}])."))
                : string.Empty;
            return TomixResult<SaveModelResult>.Fail(
                "TOMIX_BPA_VIOLATIONS",
                $"BPA check found {result.Violations.Count} violation(s) that could not be auto-fixed.{ruleErrorNotes} Fix them manually or save without --fix-bpa.",
                exitCode: 1);
        }

        return null;
    }

    private static bool SamePath(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(b))
            return false;

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string InferSerialization(string modelPath)
    {
        var extension = Path.GetExtension(modelPath);
        if (extension.Equals(".bim", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".tmsl", StringComparison.OrdinalIgnoreCase))
            return "bim";

        return "tmdl";
    }
}
