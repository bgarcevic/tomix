using System.Diagnostics;
using Tomix.Auth;
using Tomix.Core.Authentication;

namespace Tomix.App.Tests;

/// <summary>
/// Live-model QA finding: a workspace sync with no cached login stalled ~4.5 minutes with no
/// output before failing. Token acquisition must gate on the auth-state sidecar (never opening
/// the OS-keystore-backed MSAL cache when there is no recorded login) and cap the silent
/// acquisition with a hard timeout.
/// </summary>
public sealed class MsalAuthenticatorFastFailTests
{
    [Fact]
    public async Task GetTokenAsync_NoRecordedLogin_FailsFast()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tomix-auth-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var authenticator = new MsalAuthenticator(
                MsalAuthSettings.Default,
                cacheDirectory: directory,
                stateFile: Path.Combine(directory, "auth-state.json"),
                credentialFile: Path.Combine(directory, "credentials.json"));

            var stopwatch = Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<AuthenticationRequiredException>(
                () => authenticator.GetTokenAsync("powerbi://api.powerbi.com/v1.0/myorg/ws", CancellationToken.None));
            stopwatch.Stop();

            Assert.Contains("tx auth login", ex.Message);
            // The gate must not open the keystore-backed MSAL cache — that can block for minutes.
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                $"Expected an immediate failure, took {stopwatch.Elapsed}.");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task WithTimeoutAsync_HungAcquisition_SurfacesActionableError()
    {
        var hung = new TaskCompletionSource<AccessToken>();

        var ex = await Assert.ThrowsAsync<AuthenticationRequiredException>(
            () => MsalAuthenticator.WithTimeoutAsync(
                hung.Task, TimeSpan.FromMilliseconds(50), CancellationToken.None));

        Assert.Contains("Timed out", ex.Message);
        Assert.Contains("tx auth login", ex.Message);
    }

    [Fact]
    public async Task WithTimeoutAsync_CompletedAcquisition_ReturnsToken()
    {
        var token = new AccessToken("token", DateTimeOffset.UtcNow.AddHours(1));

        var result = await MsalAuthenticator.WithTimeoutAsync(
            Task.FromResult(token), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal("token", result.Token);
    }

    [Fact]
    public async Task WithTimeoutAsync_Cancelled_ThrowsCancellation_NotTimeout()
    {
        var hung = new TaskCompletionSource<AccessToken>();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => MsalAuthenticator.WithTimeoutAsync(
                hung.Task, TimeSpan.FromMinutes(5), cts.Token));
    }
}
