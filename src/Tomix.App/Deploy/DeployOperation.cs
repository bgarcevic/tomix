using Tomix.App.Diff;
using Tomix.Core.Authentication;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;
using Tomix.Core.Results;
using Tomix.Core.Rules;

namespace Tomix.App.Deploy;

/// <summary>
/// One deploy whose source model is open and already past the BPA gate (see
/// <see cref="DeployModelHandler.OpenAsync"/>). <see cref="PreviewAsync"/> and
/// <see cref="ApplyAsync"/> share that session, so previewing and then applying opens the model
/// and runs the gate once. The gate's non-fatal warnings ride on the first result returned, so a
/// preview followed by the deploy reports them once.
/// </summary>
public sealed class DeployOperation : IAsyncDisposable
{
    private readonly TomixResult<DeployModelResult>? _failure;
    private readonly IModelSession? _session;
    private readonly IModelDeploySession? _deployer;
    private readonly ModelDeployRequest? _deployRequest;
    private readonly DeployModelRequest? _request;
    private readonly string? _server;
    private readonly string? _database;
    private IReadOnlyList<TomixDiagnostic> _pendingWarnings;

    internal DeployOperation(
        IModelSession session,
        IModelDeploySession deployer,
        ModelDeployRequest deployRequest,
        DeployModelRequest request,
        string server,
        string? database,
        IReadOnlyList<TomixDiagnostic> warnings)
    {
        _session = session;
        _deployer = deployer;
        _deployRequest = deployRequest;
        _request = request;
        _server = server;
        _database = database;
        _pendingWarnings = warnings;
    }

    private DeployOperation(TomixResult<DeployModelResult> failure)
    {
        _failure = failure;
        _pendingWarnings = [];
    }

    internal static DeployOperation Failed(TomixResult<DeployModelResult> failure) => new(failure);

    /// <summary>The validation, open, or BPA gate failure, when the deploy cannot proceed.</summary>
    public TomixResult<DeployModelResult>? Failure => _failure;

    /// <summary>What this deploy would change on the target, without deploying.</summary>
    public async Task<TomixResult<DeployModelResult>> PreviewAsync(CancellationToken cancellationToken)
    {
        if (_failure is not null)
            return _failure;

        var request = _request!;
        var server = _server!;
        var database = _database;
        DiffModelResult? diff = null;
        string? diffError = null;
        bool? createsDatabase = null;

        // Without -d the deploy writes a database named after the source model, and the plan
        // resolves that name the same way, so the preview diffs the database the deploy would hit.
        try
        {
            // The plan reads the target once and returns both the target's current model
            // and the model this deploy would leave behind under the same options a real
            // deploy uses, so preserved objects never surface as changes.
            var plan = await _deployer!.GeneratePlanAsync(_deployRequest!, cancellationToken);
            database ??= plan.TargetName;

            if (!plan.TargetExists)
                // Nothing to compare against: the deploy creates the database and ships
                // the full source model.
                createsDatabase = true;
            else
                // Target first: the preview answers "what will this deploy change on the
                // target", so added/removed/old→new read in the deploy's direction. The
                // target is a processed database, so engine-computed state is ignored.
                diff = DiffModelHandler.Diff(
                    plan.Target!, plan.Planned, ignoreEngineComputedState: true);
        }
        // The deploy itself cannot run either, so this fails the preview rather than degrading it.
        catch (DeployTargetNameRequiredException ex)
        {
            return NoTargetName(ex);
        }
        // Keep the reason: "not authenticated" and "target unreachable" are different
        // situations and the preview should not conflate them.
        catch (AuthenticationRequiredException ex)
        {
            diffError = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ModelLoadException)
        {
            diffError = $"Cannot read target '{server}': {ex.InnerException?.Message ?? ex.Message}";
        }

        return Ok(new DeployModelResult(
            server, database ?? request.Model.Value, "preview", null, null, null, diff, diffError,
            createsDatabase));
    }

    /// <summary>Deploys, or with <c>--xmla</c> writes the deployment script instead.</summary>
    public async Task<TomixResult<DeployModelResult>> ApplyAsync(CancellationToken cancellationToken)
    {
        if (_failure is not null)
            return _failure;

        var request = _request!;
        var server = _server!;
        var database = _database;

        if (!string.IsNullOrWhiteSpace(request.XmlaOutput))
        {
            string script;
            try
            {
                // Preservation options require reading the target so the script matches what a
                // real deploy would execute; a full deploy is scripted offline.
                script = await _deployer!.GenerateScriptAsync(_deployRequest!, cancellationToken);
            }
            catch (DeployTargetNameRequiredException ex)
            {
                return NoTargetName(ex);
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
                return Ok(new DeployModelResult(
                    server, database ?? request.Model.Value, "script", null, "-", script));

            var fullPath = Path.GetFullPath(scriptPath);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(fullPath, script, cancellationToken).ConfigureAwait(false);
            return Ok(new DeployModelResult(
                server, database ?? request.Model.Value, "script", null, fullPath, null));
        }

        try
        {
            var result = await _deployer!.DeployAsync(_deployRequest!, cancellationToken);
            return Deployed(result);
        }
        catch (DeployTargetNameRequiredException ex)
        {
            return NoTargetName(ex);
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

    private static TomixResult<DeployModelResult> NoTargetName(DeployTargetNameRequiredException ex)
        => TomixResult<DeployModelResult>.Fail("TOMIX_DEPLOY_NO_TARGET", ex.Message, exitCode: 2,
            hint: "Pass -d/--database to name the target database.");

    /// <summary>A success carrying the gate warnings, the first time only.</summary>
    private TomixResult<DeployModelResult> Ok(DeployModelResult data)
    {
        var warnings = _pendingWarnings;
        _pendingWarnings = [];
        return TomixResult<DeployModelResult>.Ok(data, diagnostics: warnings);
    }

    /// <summary>
    /// A deploy that ran, judged by the post-deploy pass: an object the server reports in an
    /// error state fails the command (exit 1) although the model is deployed, so a pipeline
    /// cannot pass on a model whose measures do not evaluate. Incomplete objects, and a pass
    /// that could not read the target, are warnings; unprocessed objects are expected after a
    /// metadata deploy and only reported in the result.
    /// </summary>
    private TomixResult<DeployModelResult> Deployed(ModelDeployResult result)
    {
        var data = new DeployModelResult(
            result.Server, result.Database, result.Status, result.DurationMs, null, null,
            Validation: result.Validation);
        var ok = Ok(data);
        var (diagnostics, failed) = PostDeployDiagnostics(result.Database, result.Validation, result.ValidationError);
        return new TomixResult<DeployModelResult>(
            Success: !failed,
            data,
            [.. ok.Diagnostics, .. diagnostics],
            ExitCode: failed ? 1 : 0);
    }

    /// <summary>Most objects a post-deploy message names before summarising the rest.</summary>
    internal const int NamedObjectLimit = 5;

    internal static (IReadOnlyList<TomixDiagnostic> Diagnostics, bool Failed) PostDeployDiagnostics(
        string database, DeployValidation? validation, string? validationError)
    {
        if (validation is null)
        {
            return validationError is null
                ? ([], false)
                : ([
                    new TomixDiagnostic(
                        "TOMIX_DEPLOY_VALIDATION_UNAVAILABLE",
                        DiagnosticSeverity.Warning,
                        $"Deployed '{database}', but the post-deploy check could not read it: {validationError}",
                        Hint: "Run 'tx validate' against the deployed model to check it.")
                ], false);
        }

        var diagnostics = new List<TomixDiagnostic>();
        var errors = validation.Issues.Where(issue => issue.Severity == RuleSeverity.Error).ToList();
        var warnings = validation.Issues.Where(issue => issue.Severity == RuleSeverity.Warning).ToList();

        if (errors.Count > 0)
            diagnostics.Add(new TomixDiagnostic(
                "TOMIX_DEPLOY_VALIDATION_FAILED",
                DiagnosticSeverity.Error,
                $"Deployed '{database}', but the server reports {errors.Count} object(s) in an error state: {Named(errors)}.",
                Hint: "The deployed model is live with these errors. Fix them and deploy again; 'tx validate' checks a model before it is deployed."));

        if (warnings.Count > 0)
            diagnostics.Add(new TomixDiagnostic(
                "TOMIX_DEPLOY_VALIDATION_WARNINGS",
                DiagnosticSeverity.Warning,
                $"The server reports {warnings.Count} incomplete object(s) in '{database}': {Named(warnings)}."));

        return (diagnostics, errors.Count > 0);
    }

    private static string Named(IReadOnlyList<DeployedObjectIssue> issues)
    {
        var named = string.Join(", ", issues.Take(NamedObjectLimit).Select(issue => $"{issue.ObjectName} ({issue.State})"));
        return issues.Count > NamedObjectLimit ? $"{named} and {issues.Count - NamedObjectLimit} more" : named;
    }

    public ValueTask DisposeAsync() => _session?.DisposeAsync() ?? ValueTask.CompletedTask;
}
