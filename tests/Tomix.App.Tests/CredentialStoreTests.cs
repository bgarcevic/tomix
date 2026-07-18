using System.Runtime.InteropServices;
using Tomix.Auth;
using Tomix.Core.Authentication;

namespace Tomix.App.Tests;

/// <summary>
/// Cross-platform service-principal credential persistence: DPAPI on Windows, an owner-only
/// (0600) file elsewhere. Load must refuse a Unix file readable by group/other.
/// </summary>
public sealed class CredentialStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tomix-cred-tests").FullName;
    private string CredentialFile => Path.Combine(_dir, "credentials.enc");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void SaveAndLoad_RoundTripsServicePrincipalSecret()
    {
        var store = new CredentialStore(CredentialFile);
        store.Save(new AuthLoginOptions(
            AuthMethod.ServicePrincipalSecret, Tenant: "tenant", ClientId: "app", ClientSecret: "s3cret"));

        var loaded = store.Load(AuthMethod.ServicePrincipalSecret, endpoint: null);

        Assert.NotNull(loaded);
        Assert.Equal("s3cret", loaded!.ClientSecret);
        Assert.Equal("app", loaded.ClientId);
    }

    [Fact]
    public void Save_OnUnix_CreatesOwnerOnlyFile()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var store = new CredentialStore(CredentialFile);
        store.Save(new AuthLoginOptions(
            AuthMethod.ServicePrincipalSecret, Tenant: "tenant", ClientId: "app", ClientSecret: "s3cret"));

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(CredentialFile));
    }

    [Fact]
    public void Load_OnUnix_RefusesGroupOrOtherReadableFile()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var store = new CredentialStore(CredentialFile);
        store.Save(new AuthLoginOptions(
            AuthMethod.ServicePrincipalSecret, Tenant: "tenant", ClientId: "app", ClientSecret: "s3cret"));
        File.SetUnixFileMode(CredentialFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        Assert.Null(store.Load(AuthMethod.ServicePrincipalSecret, endpoint: null));
    }

    [Fact]
    public void Delete_RemovesTheCredentialFile()
    {
        var store = new CredentialStore(CredentialFile);
        store.Save(new AuthLoginOptions(
            AuthMethod.ServicePrincipalSecret, Tenant: "tenant", ClientId: "app", ClientSecret: "s3cret"));

        Assert.True(store.Delete());
        Assert.False(File.Exists(CredentialFile));
    }

    [Fact]
    public async Task GetTokenAsync_IgnoresSecretEnvVars_ForSilentRenewal()
    {
        // Regression guard for the removed env-var renewal path: even with the old variable
        // set in the process environment, an expired SP login with no saved credentials must
        // ask for a fresh 'tx auth login' instead of silently using the env secret.
        var original = Environment.GetEnvironmentVariable("TOMIX_AUTH_CLIENT_SECRET");
        Environment.SetEnvironmentVariable("TOMIX_AUTH_CLIENT_SECRET", "env-secret");
        try
        {
            var stateFile = Path.Combine(_dir, "auth-state.json");
            new AuthStateStore(stateFile).Save(new AuthState(
                AuthMethod.ServicePrincipalSecret, "app:client", "tenant", "client",
                Endpoint: null, ExpiresOn: DateTimeOffset.UtcNow.AddHours(-1)));

            var authenticator = new MsalAuthenticator(
                MsalAuthSettings.Default,
                cacheDirectory: _dir,
                stateFile: stateFile,
                credentialFile: CredentialFile);

            var ex = await Assert.ThrowsAsync<AuthenticationRequiredException>(
                () => authenticator.GetTokenAsync("powerbi://api.powerbi.com/v1.0/myorg/ws", CancellationToken.None));

            Assert.Contains("no saved credentials", ex.Message);
            Assert.Contains("--password -", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOMIX_AUTH_CLIENT_SECRET", original);
        }
    }
}
