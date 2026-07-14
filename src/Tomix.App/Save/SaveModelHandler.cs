using Tomix.App.Bpa;
using Tomix.App.Mutations;
using Tomix.Core.Bpa;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Save;

public sealed class SaveModelHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public SaveModelHandler(IEnumerable<IModelProvider> providers)
        => _providers = providers.ToList();

    public async Task<TomixResult<SaveModelResult>> HandleAsync(
        SaveModelRequest request,
        CancellationToken cancellationToken)
    {
        var provider = _providers.FirstOrDefault(p => p.CanOpen(request.Model));
        if (provider is null)
            return TomixResult<SaveModelResult>.Fail(
                code: "TOMIX_NO_PROVIDER",
                message: $"No provider can open model: {request.Model.Value}",
                exitCode: 2,
                hint: "Supported formats: TMDL folder, .bim file. For remote models, use --server and --database.");

        var serialization = string.IsNullOrWhiteSpace(request.Serialization)
            ? InferSerialization(request.Model.Value)
            : request.Serialization;

        await using var session = await provider.OpenAsync(request.Model, cancellationToken);

        var outputPath = request.OutputPath;
        if (string.IsNullOrWhiteSpace(outputPath))
            outputPath = session.SourcePath;

        if (string.IsNullOrWhiteSpace(outputPath))
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
            var export = await exporter.ExportAsync(
                new ModelExportRequest(outputPath, serialization, request.Force, request.SupportingFiles),
                cancellationToken);

            var (synced, syncTarget, syncWarning) = await WorkspaceSync.SyncAsync(
                session, request.SyncTarget, request.Force, cancellationToken);

            // A failed workspace sync leaves the mirror behind the source; render the saved
            // result but exit non-zero so CI catches the drift.
            return TomixResult<SaveModelResult>.Ok(
                new SaveModelResult(export.SavedPath, export.Format, synced, syncTarget, syncWarning),
                syncWarning is not null ? 1 : 0);
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
            rules = await BpaRuleLoader.LoadRulesetAsync(null, cancellationToken).ConfigureAwait(false);
            if (request.BpaRules is not null)
            {
                foreach (var file in request.BpaRules)
                {
                    if (!string.IsNullOrWhiteSpace(file))
                        rules = [.. rules, .. await BpaRuleLoader.LoadFromSourceAsync(file, cancellationToken).ConfigureAwait(false)];
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
        var fixResult = fixer.ApplyFixes(mutationSession, result.Violations, rules);

        if (fixResult.FixesApplied == 0 && result.Violations.Any(v => v.Severity == BpaSeverity.Error))
            return TomixResult<SaveModelResult>.Fail(
                "TOMIX_BPA_VIOLATIONS",
                $"BPA check found {result.Violations.Count} violation(s) that could not be auto-fixed. Use --skip-bpa to bypass.",
                exitCode: 1);

        return null;
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
