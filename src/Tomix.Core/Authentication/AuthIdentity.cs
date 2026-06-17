namespace Tomix.Core.Authentication;

/// <summary>
/// The signed-in account as reported by <c>auth login</c>/<c>auth status</c>.
/// <paramref name="Storage"/> is a human label for where the token cache lives
/// (e.g. "OS keystore (DPAPI)").
/// <paramref name="TokenAccount"/> and <paramref name="TokenTenantId"/> are the
/// actual account and tenant from the token, which may differ from the requested
/// values (for mismatch detection).
/// </summary>
public sealed record AuthIdentity(
    string Username,
    string? TenantId,
    AuthMethod Method,
    DateTimeOffset? ExpiresOn,
    string Storage,
    string? TokenAccount = null,
    string? TokenTenantId = null);
