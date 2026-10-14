using System.Diagnostics;
using Tomix.App.Format.M;
using Tomix.App.Models;
using Tomix.Core.Models;
using Tomix.Core.Results;
using Tomix.Core.Rules;

namespace Tomix.App.Validate;

public sealed class ValidateModelHandler
{
    private readonly IModelSessionSource _sessions;
    private readonly PowerQueryEngine? _mEngine;

    public ValidateModelHandler(IEnumerable<IModelProvider> providers)
        : this(new OneShotSessionSource(providers, noProviderHint: null))
    {
    }

    public ValidateModelHandler(IModelSessionSource sessions)
        => _sessions = sessions;

    internal ValidateModelHandler(IEnumerable<IModelProvider> providers, PowerQueryEngine mEngine)
        : this(new OneShotSessionSource(providers, noProviderHint: null))
        => _mEngine = mEngine;

    public async Task<TomixResult<ValidateModelResult>> HandleAsync(
        ValidateModelRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var opened = await ModelSessionRunner.RunAsync(
                _sessions,
                request.Model,
                async session => TomixResult<ModelSnapshot>.Ok(await session.GetSnapshotAsync(cancellationToken)),
                cancellationToken);
            if (opened.Data is not { } snapshot)
                return new TomixResult<ValidateModelResult>(false, null, opened.Diagnostics, opened.ExitCode);

            // The analysis runs on the snapshot after the session is released: parsing M can take
            // seconds on a large model, and a live session must not stay leased meanwhile.
            if (request.ServerOnly)
                return Complete(request, stopwatch, new ModelValidation.Findings([], null), snapshot.Name);

            var findings = ModelValidation.Analyze(snapshot);
            if (!request.SkipM)
            {
                var mIssues = await MSyntaxValidation.AnalyzeAsync(
                    snapshot, _mEngine ?? PowerQueryEngine.Shared, cancellationToken);
                findings = findings with { Issues = [.. findings.Issues, .. mIssues] };
            }

            return Complete(request, stopwatch, findings, snapshot.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A local model that cannot even be loaded (e.g. TMDL with unresolvable references)
            // is a validation failure, not a crash. Auth and remote-connect failures in the guard
            // stay diagnostics: they describe the connection, not the model.
            var findings = new ModelValidation.Findings(
                [ValidationRules.ModelLoadFailed.Issue(ex.Message, request.Model.Value)],
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
            .Where(issue => issue.Severity == RuleSeverity.Error)
            .ToList();
        var warnings = findings.Issues
            .Where(issue => issue.Severity != RuleSeverity.Error)
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
