namespace Tomix.Core.Authentication;

/// <summary>
/// Supplies a (silently refreshed) access token for an XMLA endpoint. Consumed by the
/// remote TOM provider when opening <c>powerbi://</c> or <c>asazure://</c> connections.
/// The scope is derived from the endpoint by the implementation.
/// </summary>
public interface IAccessTokenProvider
{
    Task<AccessToken> GetTokenAsync(string endpoint, CancellationToken cancellationToken);
}
