namespace Tomix.Core.Configuration;

/// <summary>
/// The set of preferences tomix stores in local config. Config holds preferences, never secrets.
/// </summary>
public static class ConfigKeys
{
    public const string DefaultFormat = "defaultFormat";
    public const string NoColor = "noColor";
    public const string Telemetry = "telemetry";
    public const string ActiveProfile = "activeProfile";
    public const string HideWarnings = "hideWarnings";

    /// <summary>Azure AD application (client) id used for interactive/device-code sign-in. Not a secret.</summary>
    public const string AuthClientId = "auth.clientId";

    /// <summary>Tenant id or domain used to build the authority (defaults to <c>organizations</c>).</summary>
    public const string AuthTenant = "auth.tenant";

    /// <summary>Full authority URL override (e.g. a sovereign cloud login endpoint).</summary>
    public const string AuthAuthority = "auth.authority";

    public static readonly IReadOnlyList<string> All =
    [
        DefaultFormat,
        NoColor,
        Telemetry,
        ActiveProfile,
        HideWarnings,
        AuthClientId,
        AuthTenant,
        AuthAuthority
    ];

    public static bool IsKnown(string key) => All.Contains(key);
}
