namespace Tomix.Core.Authentication;

/// <summary>
/// An OAuth access token plus its expiry, returned by <see cref="IAccessTokenProvider"/>
/// for injection into an XMLA connection.
/// </summary>
public sealed record AccessToken(string Token, DateTimeOffset ExpiresOn);
