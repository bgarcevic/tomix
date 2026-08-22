using System.Diagnostics;
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

            var targetName = string.IsNullOrWhiteSpace(request.Database)
                ? sourceDatabase.Name ?? sourceDatabase.ID
                : request.Database;

            var existing = server.Databases.FindByName(targetName);
            if (existing is not null && request.CreateOnly)
            {
                throw new InvalidOperationException(
                    $"Model '{targetName}' already exists on '{request.Server}'. Remove --create-only to overwrite.");
            }

            var status = existing is not null ? "updated" : "created";
            var script = BuildScript(sourceDatabase, existing, targetName, request, forExecution: true);
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

            sw.Stop();
            var deployName = existing?.Name ?? targetName;
            return new ModelDeployResult(request.Server, deployName, status, sw.ElapsedMilliseconds);
        }
        finally
        {
            if (server.Connected)
                server.Disconnect();
            server.Dispose();
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
        var targetName = string.IsNullOrWhiteSpace(request.Database)
            ? sourceDatabase.Name ?? sourceDatabase.ID
            : request.Database;

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

    /// <param name="forExecution">True when the script goes straight to the server, false when
    /// it is written to a file or stdout. Only executed scripts include the target's restricted
    /// information: preserved connection strings must survive a real deploy, but credentials
    /// must never leak into script output (CI artifacts, logs, working directories).</param>
    private static string BuildScript(
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
            // preserved objects reflect the target's actual current state.
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
        var endpoint = ResolveEndpoint(server);
        return endpoint.StartsWith("powerbi://", StringComparison.OrdinalIgnoreCase)
            || endpoint.StartsWith("asazure://", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ConnectAsync(
        TabularServer server,
        string endpoint,
        IAccessTokenProvider? tokenProvider,
        CancellationToken cancellationToken)
    {
        if (!ModelReference.IsLocalInstanceEndpoint(endpoint))
        {
            if (tokenProvider is null)
                throw new AuthenticationRequiredException("Not authenticated. Run 'tx auth login'.");

            var token = await tokenProvider.GetTokenAsync(endpoint, cancellationToken).ConfigureAwait(false);
            server.AccessToken = new AsAccessToken(token.Token, token.ExpiresOn.UtcDateTime);
            server.OnAccessTokenExpired = _ =>
            {
                var refreshed = tokenProvider.GetTokenAsync(endpoint, cancellationToken)
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
