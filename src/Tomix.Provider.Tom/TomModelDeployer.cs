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

        var clone = sourceDatabase.Clone();
        clone.Name = targetName;

        var dbJson = TabularJsonSerializer.SerializeDatabase(clone, new SerializeOptions());
        server.Execute(BuildCreateOrReplaceCommand(targetName, dbJson));

        sw.Stop();
        server.Disconnect();
        server.Dispose();
        return new ModelDeployResult(request.Server, targetName, status, sw.ElapsedMilliseconds);
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
    {
        if (string.IsNullOrWhiteSpace(server))
            return server;

        if (server.Contains("://", StringComparison.Ordinal) ||
            server.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase) ||
            server.StartsWith("127.0.0.1:", StringComparison.OrdinalIgnoreCase))
            return server;

        return $"powerbi://api.powerbi.com/v1.0/myorg/{server}";
    }
}
