using Tomix.App.Deps;
using Tomix.App.Diagnostics;
using Tomix.App.Get;
using Tomix.Core.Authentication;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Tests;

/// <summary>
/// The guard is the single home of the connection-failure contract: every handler that opens a
/// model must produce the same TOMIX_AUTH_REQUIRED / TOMIX_CONNECT_FAILED /
/// TOMIX_DATABASE_NOT_FOUND diagnostics for the same failures.
/// </summary>
public sealed class ProviderConnectionGuardTests
{
    private static readonly ModelReference RemoteRef =
        ModelReference.Remote("powerbi://api.powerbi.com/v1.0/myorg/Workspace", "Sales");

    private static readonly ModelReference LocalRef = new("./model.tmdl");

    private static Task<TomixResult<string>> Throwing(Exception ex)
        => Task.FromException<TomixResult<string>>(ex);

    [Fact]
    public async Task RunAsync_AuthenticationRequired_MapsToAuthRequired()
    {
        var result = await ProviderConnectionGuard.RunAsync(
            RemoteRef, () => Throwing(new AuthenticationRequiredException("login please")));

        Assert.False(result.Success);
        Assert.Equal("TOMIX_AUTH_REQUIRED", result.Diagnostics[0].Code);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_RemoteFailure_MapsToConnectFailed()
    {
        var result = await ProviderConnectionGuard.RunAsync(
            RemoteRef, () => Throwing(new InvalidOperationException("server down")));

        Assert.False(result.Success);
        Assert.Equal("TOMIX_CONNECT_FAILED", result.Diagnostics[0].Code);
        Assert.Contains(RemoteRef.Value, result.Diagnostics[0].Message);
    }

    [Fact]
    public async Task RunAsync_RemoteDatabaseNotFound_MapsToDatabaseNotFound()
    {
        var result = await ProviderConnectionGuard.RunAsync(
            RemoteRef, () => Throwing(new ModelConnectionException(
                ModelConnectionFailureKind.DatabaseNotFound,
                "Database not found on endpoint 'x'.")));

        Assert.False(result.Success);
        Assert.Equal("TOMIX_DATABASE_NOT_FOUND", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task RunAsync_DoesNotClassifyDatabaseFromExceptionMessage()
    {
        var result = await ProviderConnectionGuard.RunAsync(
            RemoteRef, () => Throwing(new InvalidOperationException("Database not found on endpoint 'x'.")));

        Assert.False(result.Success);
        Assert.Equal("TOMIX_CONNECT_FAILED", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task RunAsync_LocalFailure_Propagates()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProviderConnectionGuard.RunAsync(LocalRef, () => Throwing(new InvalidOperationException("boom"))));
    }

    [Fact]
    public async Task RunAsync_NullModelFailure_Propagates()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProviderConnectionGuard.RunAsync<string>(model: null, () => Throwing(new InvalidOperationException("boom"))));
    }

    [Fact]
    public async Task RunAsync_Cancellation_Propagates()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ProviderConnectionGuard.RunAsync(RemoteRef, () => Throwing(new OperationCanceledException())));
    }

    [Fact]
    public async Task RunAsync_Success_PassesResultThrough()
    {
        var result = await ProviderConnectionGuard.RunAsync(
            RemoteRef, () => Task.FromResult(TomixResult<string>.Ok("data")));

        Assert.True(result.Success);
        Assert.Equal("data", result.Data);
    }

    // Regression for the review finding that get/deps lacked connection-error handling and fell
    // through to TOMIX_UNEXPECTED while sibling commands produced actionable diagnostics.

    [Fact]
    public async Task Get_MapsAuthenticationRequired_ToDiagnostic()
    {
        var handler = new GetModelHandler([new ThrowingProvider(new AuthenticationRequiredException("login please"))]);

        var result = await handler.HandleAsync(new GetModelRequest(RemoteRef, "Sales", null, null), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_AUTH_REQUIRED", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task Deps_MapsRemoteFailure_ToConnectFailed()
    {
        var handler = new DepsModelHandler([new ThrowingProvider(new InvalidOperationException("server down"))]);

        var result = await handler.HandleAsync(
            new DepsModelRequest(
                RemoteRef, "Sales", Type: null,
                UpstreamOnly: false, DownstreamOnly: false, Deep: false,
                Unused: false, HiddenOnly: false, MaxDepth: 0),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_CONNECT_FAILED", result.Diagnostics[0].Code);
    }

    private sealed class ThrowingProvider : IModelProvider
    {
        private readonly Exception _error;

        public ThrowingProvider(Exception error) => _error = error;

        public bool CanOpen(ModelReference reference) => reference.IsRemote;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken cancellationToken)
            => Task.FromException<IModelSession>(_error);
    }
}
