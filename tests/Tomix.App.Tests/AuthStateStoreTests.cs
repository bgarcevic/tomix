using Tomix.Auth;
using Tomix.Core.Authentication;

namespace Tomix.App.Tests;

/// <summary>
/// The auth sidecar (<c>auth-state.json</c>) outlives CLI upgrades, so its shape is a contract:
/// PascalCase properties and the method written as its enum name.
/// </summary>
public sealed class AuthStateStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tomix-auth-state-tests").FullName;
    private string StateFile => Path.Combine(_dir, "auth-state.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Save_WritesMethodAsEnumName()
    {
        var store = new AuthStateStore(StateFile);
        store.Save(new AuthState(AuthMethod.ServicePrincipalSecret, "app", "tenant", "client", null, null));

        var json = File.ReadAllText(StateFile);

        Assert.Contains("\"Method\": \"ServicePrincipalSecret\"", json);
        Assert.Contains("\"Username\": \"app\"", json);
    }

    [Fact]
    public void Load_ReadsFileWrittenByEarlierVersions()
    {
        File.WriteAllText(StateFile, """
            {
              "Method": "DeviceCode",
              "Username": "user@contoso.com",
              "TenantId": "tenant",
              "ClientId": null,
              "Endpoint": "powerbi://api.powerbi.com/v1.0/myorg/Sales",
              "ExpiresOn": "2026-01-02T03:04:05+00:00"
            }
            """);

        var loaded = new AuthStateStore(StateFile).Load();

        Assert.Equal(
            new AuthState(
                AuthMethod.DeviceCode,
                "user@contoso.com",
                "tenant",
                null,
                "powerbi://api.powerbi.com/v1.0/myorg/Sales",
                new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)),
            loaded);
    }
}
