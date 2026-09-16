using System.Diagnostics;
using Tomix.App.Models;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Validate;

public sealed class ValidateModelHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public ValidateModelHandler(IEnumerable<IModelProvider> providers)
        => _providers = providers.ToList();

    public async Task<TomixResult<ValidateModelResult>> HandleAsync(
        ValidateModelRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await ModelSessionRunner.RunAsync(_providers, request.Model, async session =>
            {
                var snapshot = await session.GetSnapshotAsync(cancellationToken);

                var findings = request.ServerOnly
                    ? new ModelValidation.Findings([], null)
                    : ModelValidation.Analyze(snapshot);
                return Complete(request, stopwatch, findings, snapshot.Name);
            }, noProviderMessage: null, noProviderHint: null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A local model that cannot even be loaded (e.g. TMDL with unresolvable references)
            // is a validation failure, not a crash. Auth and remote-connect failures in the guard
            // stay diagnostics: they describe the connection, not the model.
            var findings = new ModelValidation.Findings(
                [new ValidationIssue(ValidationSeverity.Error, "TOMIX_MODEL_LOAD_FAILED", ex.Message, request.Model.Value, Expression: null)],
                null);
            return Complete(request, stopwatch, findings, request.Model.Value);
        }
    }

    private static TomixResult<ValidateModelResult> Complete(
        ValidateModelRequest request,
        Stopwatch stopwatch,
        ModelValidation.Findings findings,
        string modelName)
    {
        stopwatch.Stop();

        var errors = findings.Issues
            .Where(issue => issue.Severity == ValidationSeverity.Error)
            .ToList();
        var warnings = findings.Issues
            .Where(issue => issue.Severity != ValidationSeverity.Error)
            .ToList();

        var result = new ValidateModelResult(
            ModelName: modelName,
            Valid: errors.Count == 0,
            DurationMs: Math.Max(0, stopwatch.ElapsedMilliseconds),
            Errors: errors,
            Warnings: request.NoWarnings ? [] : warnings,
            MeasureNames: findings.MeasureNames);

        return TomixResult<ValidateModelResult>.Ok(result, exitCode: result.Valid ? 0 : 1);
    }
}
