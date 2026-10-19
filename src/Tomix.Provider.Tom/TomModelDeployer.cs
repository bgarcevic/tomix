using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Authentication;
using Tomix.Core.Models;
using AsAccessToken = Microsoft.AnalysisServices.AccessToken;
using TabularDatabase = Microsoft.AnalysisServices.Tabular.Database;
using TabularJsonSerializer = Microsoft.AnalysisServices.Tabular.JsonSerializer;
using TabularServer = Microsoft.AnalysisServices.Tabular.Server;

namespace Tomix.Provider.Tom;

public static class TomModelDeployer
{
    public static async Task<ModelDeployResult> DeployAsync(
        Database sourceDatabase,
        ModelDeployRequest request,
        IAccessTokenProvider? tokenProvider,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();

        var server = new TabularServer();
        try
        {
            await ConnectAsync(server, request.Server, tokenProvider, cancellationToken).ConfigureAwait(false);

            var targetName = ResolveTargetName(sourceDatabase, request);

            var existing = server.Databases.FindByName(targetName);
            if (existing is not null && request.CreateOnly)
            {
                throw new InvalidOperationException(
                    $"Model '{targetName}' already exists on '{request.Server}'. Remove --create-only to overwrite.");
            }

            if (request.RollbackOnError && existing is not null)
                return DeployInTransaction(server, sourceDatabase, existing, request, sw);

            var status = existing is not null ? "updated" : "created";
            var script = BuildScript(sourceDatabase, existing, targetName, request, forExecution: true);
            Execute(server, script);

            var deployName = existing?.Name ?? targetName;
            var (validation, validationError) = InspectDeployed(server, deployName);

            // A new database cannot be created inside a transaction (ADR 0005), so undoing it
            // means dropping it: the target held no such database before, and holds none again.
            if (request.RollbackOnError && ShouldRollBack(validation))
            {
                Execute(server, DeleteDatabaseScript(deployName));
                status = ModelDeployResult.RolledBack;
            }

            sw.Stop();
            return new ModelDeployResult(
                request.Server, deployName, status, sw.ElapsedMilliseconds, validation, validationError);
        }
        finally
        {
            if (server.Connected)
                server.Disconnect();
            server.Dispose();
        }
    }

    /// <summary>
    /// <c>--rollback-on-error</c> against an existing database (ADR 0005). The Power BI endpoint
    /// commits a database-level <c>createOrReplace</c> on its own, ending any open transaction,
    /// so the merged model is applied as object-level changes instead: copied over the live
    /// model and saved inside an explicit transaction, where the server already reports the
    /// uncommitted objects' states. Errors roll the transaction back; otherwise it commits.
    /// </summary>
    private static ModelDeployResult DeployInTransaction(
        TabularServer server,
        Database sourceDatabase,
        TabularDatabase existing,
        ModelDeployRequest request,
        Stopwatch sw)
    {
        existing.Refresh(true);
        var merged = BuildMergedDatabase(sourceDatabase, existing, request);
        if (merged.CompatibilityLevel != existing.CompatibilityLevel)
        {
            // A database-level change commits on its own, so it cannot be undone with the rest.
            throw new InvalidOperationException(
                $"--rollback-on-error cannot change the compatibility level of '{existing.Name}' "
                + $"({existing.CompatibilityLevel} to {merged.CompatibilityLevel}). Deploy once without it to change the level.");
        }

        server.BeginTransaction();
        var open = true;
        try
        {
            merged.Model.CopyTo(existing.Model);
            existing.Model.SaveChanges();

            DeployValidation? validation = null;
            string? validationError = null;
            try
            {
                // Inside the transaction the server reports the uncommitted objects' states.
                existing.Refresh(true);
                validation = TomDeployValidator.Inspect(existing);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                validationError = ex.InnerException?.Message ?? ex.Message;
            }

            var rollBack = ShouldRollBack(validation);
            open = false;
            if (rollBack)
                server.RollbackTransaction();
            else
                server.CommitTransaction();

            sw.Stop();
            return new ModelDeployResult(
                request.Server, existing.Name, rollBack ? ModelDeployResult.RolledBack : "updated",
                sw.ElapsedMilliseconds, validation, validationError);
        }
        catch when (open)
        {
            // Anything that fails before the decision leaves the target as it was.
            TryRollback(server);
            throw;
        }
    }

    private static void TryRollback(TabularServer server)
    {
        try
        {
            server.RollbackTransaction();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The original failure is the one to report; an ended session rolls back as well.
        }
    }

    /// <summary>
    /// Under <c>--rollback-on-error</c>, an object in an error state undoes the deploy, and so
    /// does a check that could not run: the flag promises a broken model never lands, and an
    /// unchecked one might be broken. Warnings and unprocessed objects do not.
    /// </summary>
    internal static bool ShouldRollBack(DeployValidation? validation)
        => validation is null || validation.Errors > 0;

    /// <summary>
    /// The model a deploy leaves on <paramref name="existing"/>, as a detached database: the
    /// same merge <see cref="BuildScript"/> executes, with restricted information so preserved
    /// connection strings survive.
    /// </summary>
    internal static TabularDatabase BuildMergedDatabase(
        Database sourceDatabase, TabularDatabase existing, ModelDeployRequest request)
    {
        var options = request.EffectiveOptions;
        var clone = sourceDatabase.Clone();
        clone.Name = existing.Name;
        var restricted = new SerializeOptions { IncludeRestrictedInformation = true };

        var node = TmslDeployScriptBuilder.BuildDatabaseNode(
            TabularJsonSerializer.SerializeDatabase(clone, restricted),
            options.RequiresTargetRead ? TabularJsonSerializer.SerializeDatabase(existing, restricted) : null,
            existing.Name,
            existing.ID,
            options,
            stripRoleMemberIds: IsCloudEndpoint(request.Server));

        return TabularJsonSerializer.DeserializeDatabase(node.ToJsonString());
    }

    private static void Execute(TabularServer server, string script)
    {
        var results = server.Execute(script);

        // AMO's Execute returns a result collection that may contain errors/warnings even
        // when no exception is thrown. Surface them so deploys don't silently no-op.
        var serverErrors = new List<string>();
        XmlaResultHelper.ExtractMessages(results, serverErrors);
        if (serverErrors.Count > 0)
        {
            var message = serverErrors.Count == 1
                ? serverErrors[0]
                : string.Join("; ", serverErrors);
            throw new InvalidOperationException(message);
        }
    }

    internal static string DeleteDatabaseScript(string name)
        => new JsonObject
        {
            ["delete"] = new JsonObject { ["object"] = new JsonObject { ["database"] = name } }
        }.ToJsonString();

    /// <summary>
    /// Re-reads the deployed database on the open connection and runs the post-deploy pass
    /// (#237). The deploy has already succeeded, so a failed read is reported, not thrown.
    /// </summary>
    private static (DeployValidation? Validation, string? Error) InspectDeployed(TabularServer server, string name)
    {
        try
        {
            // The server object predates the deploy: a created database is not in its
            // collection yet, and an updated one still holds the pre-deploy object states.
            server.Refresh();
            var deployed = server.Databases.FindByName(name)
                ?? throw new InvalidOperationException($"Database '{name}' was not found after the deploy.");
            deployed.Refresh(true);
            return (TomDeployValidator.Inspect(deployed), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, ex.InnerException?.Message ?? ex.Message);
        }
    }

    /// <summary>
    /// Generates the deployment script. When the options preserve target-owned objects, the
    /// target database is read so the script matches what <see cref="DeployAsync"/> would
    /// execute; a full deploy (or a target that does not exist yet) is scripted offline.
    /// </summary>
    public static async Task<string> GenerateScriptAsync(
        Database sourceDatabase,
        ModelDeployRequest request,
        IAccessTokenProvider? tokenProvider,
        CancellationToken cancellationToken)
    {
        var targetName = ResolveTargetName(sourceDatabase, request);

        if (!request.EffectiveOptions.RequiresTargetRead)
            return BuildScript(sourceDatabase, existing: null, targetName, request, forExecution: false);

        var server = new TabularServer();
        try
        {
            await ConnectAsync(server, request.Server, tokenProvider, cancellationToken).ConfigureAwait(false);
            var existing = server.Databases.FindByName(targetName);
            return BuildScript(sourceDatabase, existing, targetName, request, forExecution: false);
        }
        finally
        {
            if (server.Connected)
                server.Disconnect();
            server.Dispose();
        }
    }

    /// <summary>
    /// The database a deploy writes: the requested name, otherwise the source model's own name
    /// (or ID). A model with neither, such as a bare TMDL folder, has nothing to deploy as, so it
    /// fails with a request for <c>-d</c> instead of targeting a database named "".
    /// </summary>
    internal static string ResolveTargetName(Database sourceDatabase, ModelDeployRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Database))
            return request.Database;
        if (!string.IsNullOrWhiteSpace(sourceDatabase.Name))
            return sourceDatabase.Name;
        if (!string.IsNullOrWhiteSpace(sourceDatabase.ID))
            return sourceDatabase.ID;
        throw new DeployTargetNameRequiredException();
    }

    /// <summary>
    /// Reads the target once and returns the two sides a deploy preview compares: the target's current
    /// model and the model this deploy would leave behind under the request's options. Unlike
    /// <see cref="GenerateScriptAsync"/> this always connects — even for a full deploy — because
    /// the target snapshot is one half of the answer.
    /// </summary>
    public static async Task<ModelDeployPlan> GeneratePlanAsync(
        Database sourceDatabase,
        ModelDeployRequest request,
        IAccessTokenProvider? tokenProvider,
        CancellationToken cancellationToken)
    {
        var targetName = ResolveTargetName(sourceDatabase, request);

        var server = new TabularServer();
        try
        {
            await ConnectAsync(server, request.Server, tokenProvider, cancellationToken).ConfigureAwait(false);
            var existing = server.Databases.FindByName(targetName);
            return BuildPlan(sourceDatabase, existing, targetName, request);
        }
        finally
        {
            if (server.Connected)
                server.Disconnect();
            server.Dispose();
        }
    }

    /// <summary>
    /// Pure plan construction, mirroring <c>BuildScript(forExecution: false)</c>: the same merge
    /// produces the deployed model, so what the preview reports and what the deploy sends cannot
    /// drift. Restricted information stays out on both sides — the plan feeds a diff, not a
    /// server — and the merged node is round-tripped back through TOM so the planned model is
    /// summarized by exactly the same projection as the target.
    /// </summary>
    internal static ModelDeployPlan BuildPlan(
        Database sourceDatabase,
        TabularDatabase? existing,
        string targetName,
        ModelDeployRequest request)
    {
        var options = request.EffectiveOptions;
        var deployName = existing?.Name ?? targetName;

        var clone = sourceDatabase.Clone();
        clone.Name = deployName;

        var serializeOptions = new SerializeOptions
        {
            SplitMultilineStrings = true,
            IncludeRestrictedInformation = false
        };

        var sourceJson = TabularJsonSerializer.SerializeDatabase(clone, serializeOptions);

        string? targetJson = null;
        ModelSnapshot? targetSnapshot = null;
        if (existing is not null)
        {
            // The Databases collection is shallow until refreshed; read the full model once and
            // use it for both the merge and the target snapshot. A detached database (an
            // in-memory fixture) has nothing to refresh from and is already complete.
            if (existing.Server is not null)
                existing.Refresh(true);
            targetJson = TabularJsonSerializer.SerializeDatabase(existing, serializeOptions);
            targetSnapshot = TomModelSummarizer.Snapshot(existing, deployName);
        }

        var merged = TmslDeployScriptBuilder.BuildDatabaseNode(
            sourceJson,
            targetJson,
            deployName,
            existing?.ID,
            options,
            stripRoleMemberIds: IsCloudEndpoint(request.Server));

        var planned = TabularJsonSerializer.DeserializeDatabase(merged.ToJsonString());
        var plannedSnapshot = TomModelSummarizer.Snapshot(planned, deployName);

        return new ModelDeployPlan(existing is not null, targetSnapshot, plannedSnapshot, targetName);
    }

    /// <param name="forExecution">True when the script goes straight to the server, false when
    /// it is written to a file or stdout. Only executed scripts include the target's restricted
    /// information: preserved connection strings must survive a real deploy, but credentials
    /// must never leak into script output (CI artifacts, logs, working directories).</param>
    internal static string BuildScript(
        Database sourceDatabase,
        TabularDatabase? existing,
        string targetName,
        ModelDeployRequest request,
        bool forExecution)
    {
        var options = request.EffectiveOptions;

        // Power BI rejects XMLA deploys that change a dataset's name ("you can't rename this
        // dataset"), even a casing difference. When the target already exists, pin the deployed
        // name to the existing dataset's actual name so createOrReplace is never treated as a
        // rename. For new datasets, use the requested name.
        var deployName = existing?.Name ?? targetName;

        var clone = sourceDatabase.Clone();
        clone.Name = deployName;

        // Restricted information mirrors the target side below: a direct deploy must carry the
        // source's full connection strings (--deploy-connections shipping a credential-less
        // data source breaks the target's connection), while script output must never hold
        // credentials.
        var sourceJson = TabularJsonSerializer.SerializeDatabase(
            clone,
            new SerializeOptions
            {
                SplitMultilineStrings = !forExecution,
                IncludeRestrictedInformation = forExecution
            });

        string? targetJson = null;
        if (existing is not null && options.RequiresTargetRead)
        {
            // The Databases collection is shallow until refreshed; read the full model so
            // preserved objects reflect the target's actual current state. A detached database
            // (an in-memory fixture) has nothing to refresh from and is already complete.
            if (existing.Server is not null)
                existing.Refresh(true);

            targetJson = TabularJsonSerializer.SerializeDatabase(
                existing,
                new SerializeOptions { IncludeRestrictedInformation = forExecution });
        }

        return TmslDeployScriptBuilder.Build(
            sourceJson,
            targetJson,
            deployName,
            existing?.ID,
            options,
            stripRoleMemberIds: IsCloudEndpoint(request.Server));
    }

    /// <summary>Power BI and Azure AS assign role-member IDs service-side; shipping stale IDs
    /// makes redeploys fail, so they are stripped for cloud targets.</summary>
    private static bool IsCloudEndpoint(string server)
    {
        var endpoint = ModelReference.DataSourceOf(ResolveEndpoint(server));
        return endpoint.StartsWith("powerbi://", StringComparison.OrdinalIgnoreCase)
            || endpoint.StartsWith("asazure://", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ConnectAsync(
        TabularServer server,
        string endpoint,
        IAccessTokenProvider? tokenProvider,
        CancellationToken cancellationToken)
    {
        if (ModelReference.RequiresAccessTokenFor(endpoint))
        {
            if (tokenProvider is null)
                throw new AuthenticationRequiredException("Not authenticated. Run 'tx auth login'.");

            var token = await tokenProvider.GetTokenAsync(ModelReference.DataSourceOf(endpoint), cancellationToken).ConfigureAwait(false);
            server.AccessToken = new AsAccessToken(token.Token, token.ExpiresOn.UtcDateTime);
            server.OnAccessTokenExpired = _ =>
            {
                var refreshed = tokenProvider.GetTokenAsync(ModelReference.DataSourceOf(endpoint), cancellationToken)
                    .ConfigureAwait(false).GetAwaiter().GetResult();
                return new AsAccessToken(refreshed.Token, refreshed.ExpiresOn.UtcDateTime);
            };
        }

        server.Connect(XmlaConnectionString.Build(endpoint));
    }

    public static string ResolveEndpoint(string server)
        => string.IsNullOrWhiteSpace(server)
            ? server
            : ModelReference.NormalizeEndpoint(server);
}
