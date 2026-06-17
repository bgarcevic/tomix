using Tomix.App.Config;
using Tomix.Auth;
using Tomix.Core.Configuration;

namespace Tomix.App.Auth;

/// <summary>
/// Resolves Azure AD client settings. Precedence: explicit override → environment variable →
/// local config → built-in default.
/// </summary>
public static class AuthSettingsFactory
{
    public static MsalAuthSettings Resolve(string? clientIdOverride = null, string? tenant = null)
    {
        var config = new TomixConfigStore().Load();

        var clientId = FirstNonEmpty(
            clientIdOverride,
            Environment.GetEnvironmentVariable("TOMIX_AUTH_CLIENT_ID"),
            ConfigValue(config, ConfigKeys.AuthClientId),
            MsalAuthSettings.DefaultClientId)!;

        var authority = ConfigValue(config, ConfigKeys.AuthAuthority);
        if (string.IsNullOrWhiteSpace(authority))
        {
            var resolvedTenant = FirstNonEmpty(
                tenant,
                Environment.GetEnvironmentVariable("TOMIX_AUTH_TENANT"),
                ConfigValue(config, ConfigKeys.AuthTenant));

            authority = string.IsNullOrWhiteSpace(resolvedTenant)
                ? MsalAuthSettings.DefaultAuthority
                : $"https://login.microsoftonline.com/{resolvedTenant}";
        }

        return new MsalAuthSettings(clientId, authority);
    }

    private static string? ConfigValue(IDictionary<string, string> config, string key)
        => config.TryGetValue(key, out var value) ? value : null;

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
