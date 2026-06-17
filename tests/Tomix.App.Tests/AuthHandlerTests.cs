using Tomix.App.Auth;
using Tomix.Core.Authentication;

namespace Tomix.App.Tests;

public sealed class AuthHandlerTests
{
    private static readonly AuthIdentity SampleIdentity = new(
        "bokg@contoso.com", "tenant-id", AuthMethod.Interactive, DateTimeOffset.UtcNow.AddHours(1), "OS keystore (DPAPI)");

    [Fact]
    public async Task LoginAsync_ReturnsIdentity_OnSuccess()
    {
        var handler = new AuthHandler(new FakeAuthenticator(identity: SampleIdentity));

        var result = await handler.LoginAsync(new AuthLoginOptions(AuthMethod.Interactive), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("bokg@contoso.com", result.Data!.Identity.Username);
    }

    [Fact]
    public async Task LoginAsync_Fails_WithAuthFailed_WhenAuthenticatorThrows()
    {
        var handler = new AuthHandler(new FakeAuthenticator(loginError: new InvalidOperationException("boom")));

        var result = await handler.LoginAsync(new AuthLoginOptions(AuthMethod.DeviceCode), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_AUTH_FAILED", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task LoginAsync_Propagates_Cancellation()
    {
        var handler = new AuthHandler(new FakeAuthenticator(loginError: new OperationCanceledException()));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => handler.LoginAsync(new AuthLoginOptions(AuthMethod.Interactive), CancellationToken.None));
    }

    [Fact]
    public async Task StatusAsync_ReportsLoggedIn_WhenIdentityPresent()
    {
        var handler = new AuthHandler(new FakeAuthenticator(identity: SampleIdentity));

        var result = await handler.StatusAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Data!.LoggedIn);
        Assert.Equal("bokg@contoso.com", result.Data.Identity!.Username);
    }

    [Fact]
    public async Task StatusAsync_ReportsNotLoggedIn_WhenNoIdentity()
    {
        var handler = new AuthHandler(new FakeAuthenticator(identity: null));

        var result = await handler.StatusAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Data!.LoggedIn);
        Assert.Null(result.Data.Identity);
    }

    [Fact]
    public async Task LogoutAsync_ReportsExisted_WhenSignedIn()
    {
        var authenticator = new FakeAuthenticator(identity: SampleIdentity);
        var handler = new AuthHandler(authenticator);

        var result = await handler.LogoutAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Data!.Existed);
        Assert.True(authenticator.LoggedOut);
    }

    private sealed class FakeAuthenticator : IAuthenticator
    {
        private readonly AuthIdentity? _identity;
        private readonly Exception? _loginError;

        public FakeAuthenticator(AuthIdentity? identity = null, Exception? loginError = null)
        {
            _identity = identity;
            _loginError = loginError;
        }

        public bool LoggedOut { get; private set; }

        public Task<AuthIdentity> LoginAsync(AuthLoginOptions options, CancellationToken cancellationToken)
            => _loginError is not null
                ? Task.FromException<AuthIdentity>(_loginError)
                : Task.FromResult(_identity!);

        public Task<AuthIdentity?> StatusAsync(CancellationToken cancellationToken)
            => Task.FromResult(_identity);

        public Task<bool> LogoutAsync(CancellationToken cancellationToken)
        {
            LoggedOut = true;
            return Task.FromResult(_identity is not null);
        }
    }
}
