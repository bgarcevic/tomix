using System.Diagnostics;
using System.Text.Json;
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
        await ConnectAsync(server, request.Server, tokenProvider, cancellationToken).ConfigureAwait(false);

        var targetName = string.IsNullOrWhiteSpace(request.Database)
            ? sourceDatabase.Name ?? sourceDatabase.ID
            : request.Database;

        var existing = server.Databases.FindByName(targetName);
        if (existing is not null && request.CreateOnly)
        {
            server.Disconnect();
            server.Dispose();
            throw new InvalidOperationException(
                $"Model '{targetName}' already exists on '{request.Server}'. Remove --create-only to overwrite.");
        }

        var status = existing is not null ? "updated" : "created";

        // Power BI rejects XMLA deploys that change a dataset's name ("you can't rename this
        // dataset"), even a casing difference. When the target already exists, pin the deployed
        // name to the existing dataset's actual name so createOrReplace is never treated as a
        // rename. For new datasets, use the requested name.
        var deployName = existing?.Name ?? targetName;

        var clone = sourceDatabase.Clone();
        clone.Name = deployName;

        var dbJson = TabularJsonSerializer.SerializeDatabase(clone, new SerializeOptions());
        var results = server.Execute(BuildCreateOrReplaceCommand(deployName, dbJson));

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
        server.Disconnect();
        server.Dispose();
        return new ModelDeployResult(request.Server, deployName, status, sw.ElapsedMilliseconds);
    }

    public static string GenerateScript(Database sourceDatabase, ModelDeployRequest request)
    {
        var targetName = string.IsNullOrWhiteSpace(request.Database)
            ? sourceDatabase.Name ?? sourceDatabase.ID
            : request.Database;

        var clone = sourceDatabase.Clone();
        if (!string.IsNullOrWhiteSpace(targetName) && clone.Name != targetName)
            clone.Name = targetName;

        var dbJson = TabularJsonSerializer.SerializeDatabase(
            clone,
            new SerializeOptions { SplitMultilineStrings = true });

        return BuildCreateOrReplaceCommand(targetName, dbJson);
    }

    private static string BuildCreateOrReplaceCommand(string databaseName, string databaseJson)
    {
        var escapedName = System.Text.Json.JsonSerializer.Serialize(databaseName);
        return $"{{\"createOrReplace\":{{\"object\":{{\"database\":{escapedName}}},\"database\":{databaseJson}}}}}";
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

        server.Connect($"Data Source={ResolveEndpoint(endpoint)}");
    }

    public static string ResolveEndpoint(string server)
        => string.IsNullOrWhiteSpace(server)
            ? server
            : ModelReference.NormalizeEndpoint(server);
}
