namespace Tomix.Core.Authentication;

/// <summary>
/// Drives the <c>auth</c> command: signs in, reports the cached identity, and signs out.
/// Implemented outside Core (in Tomix.Auth) so Core stays free of MSAL.
/// </summary>
public interface IAuthenticator
{
    /// <summary>Acquire and cache a token, returning the signed-in identity.</summary>
    Task<AuthIdentity> LoginAsync(AuthLoginOptions options, CancellationToken cancellationToken);

    /// <summary>The cached identity, or <c>null</c> when no account is signed in.</summary>
    Task<AuthIdentity?> StatusAsync(CancellationToken cancellationToken);

    /// <summary>Clear all cached credentials. Returns <c>true</c> if anything was removed.</summary>
    Task<bool> LogoutAsync(CancellationToken cancellationToken);
}
