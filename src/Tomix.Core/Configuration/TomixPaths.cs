namespace Tomix.Core.Configuration;

/// <summary>
/// Resolves the well-known on-disk locations tomix uses for local state.
/// Centralised so commands and handlers agree on a single config directory.
/// </summary>
public static class TomixPaths
{
    /// <summary>The tomix config directory, <c>~/.tomix</c> (or <c>./.tomix</c> when no home is available).</summary>
    public static string ConfigDirectory
    {
        get
        {
            var overrideDirectory = Environment.GetEnvironmentVariable("TOMIX_CONFIG_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDirectory))
                return Path.GetFullPath(overrideDirectory);

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            return string.IsNullOrWhiteSpace(home)
                ? Path.Combine(Environment.CurrentDirectory, ".tomix")
                : Path.Combine(home, ".tomix");
        }
    }

    /// <summary>The local preferences file, <c>~/.tomix/config.json</c>.</summary>
    public static string ConfigFile => Path.Combine(ConfigDirectory, "config.json");

    /// <summary>The directory holding the MSAL token cache and auth metadata, <c>~/.tomix/auth</c>.</summary>
    public static string AuthDirectory => Path.Combine(ConfigDirectory, "auth");

    /// <summary>Sidecar metadata for the cached login (method/account/tenant), <c>~/.tomix/auth/auth-state.json</c>.</summary>
    public static string AuthStateFile => Path.Combine(AuthDirectory, "auth-state.json");
}
