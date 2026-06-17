namespace Tomix.Auth;

/// <summary>
/// Resolved Azure AD application settings for the interactive/device-code flows. The CLI builds
/// this from (in precedence order) command flags, environment variables, local config, and the
/// built-in defaults below. Service-principal flows carry their own client id/tenant per request.
/// </summary>
public sealed record MsalAuthSettings(string ClientId, string Authority)
{
    /// <summary>
    /// Well-known "Power BI Desktop" first-party public client. It is on Microsoft's documented
    /// list of approved Power BI client apps (see the Power Platform "apps to allow" admin guide),
    /// so the Power BI XMLA / Analysis Services engine trusts its tokens out of the box — no app
    /// registration or admin consent needed. It is a public client with an <c>http://localhost</c>
    /// loopback redirect. (The Azure PowerShell client <c>1950a258-…</c> can also mint Power BI
    /// tokens accepted by the REST API, but the XMLA endpoint rejects them, so it is unsuitable
    /// here.) Override via <c>--client-id</c> / <c>TOMIX_AUTH_CLIENT_ID</c> / <c>auth.clientId</c>
    /// to use an organisation-owned app.
    /// </summary>
    public const string DefaultClientId = "7f67af8a-fedc-4b08-8b4e-37c4d127b6cf";

    public const string DefaultAuthority = "https://login.microsoftonline.com/organizations";

    public static MsalAuthSettings Default { get; } = new(DefaultClientId, DefaultAuthority);
}
