using Tomix.Core.Authentication;
using Tomix.Core.Results;

namespace Tomix.App.Auth;

/// <summary>
/// Application use case behind the <c>auth</c> command. Delegates to an <see cref="IAuthenticator"/>
/// (the MSAL implementation is injected from the CLI) and shapes the outcome into <see cref="TomixResult{T}"/>.
/// </summary>
public sealed class AuthHandler
{
    private readonly IAuthenticator _authenticator;

    public AuthHandler(IAuthenticator authenticator) => _authenticator = authenticator;

    public async Task<TomixResult<AuthLoginResult>> LoginAsync(
        AuthLoginOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            var identity = await _authenticator.LoginAsync(options, cancellationToken);
            return TomixResult<AuthLoginResult>.Ok(new AuthLoginResult(identity));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return TomixResult<AuthLoginResult>.Fail("TOMIX_AUTH_FAILED", $"Authentication failed: {ex.Message}");
        }
    }

    public async Task<TomixResult<AuthStatusResult>> StatusAsync(CancellationToken cancellationToken)
    {
        var identity = await _authenticator.StatusAsync(cancellationToken);
        return TomixResult<AuthStatusResult>.Ok(new AuthStatusResult(identity is not null, identity));
    }

    public async Task<TomixResult<AuthLogoutResult>> LogoutAsync(CancellationToken cancellationToken)
    {
        var existed = await _authenticator.LogoutAsync(cancellationToken);
        return TomixResult<AuthLogoutResult>.Ok(new AuthLogoutResult(existed));
    }
}

public sealed record AuthLoginResult(AuthIdentity Identity);

public sealed record AuthStatusResult(bool LoggedIn, AuthIdentity? Identity);

public sealed record AuthLogoutResult(bool Existed);
