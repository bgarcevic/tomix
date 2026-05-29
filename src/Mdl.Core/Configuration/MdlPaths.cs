namespace Mdl.Core.Configuration;

/// <summary>
/// Resolves the well-known on-disk locations MDL uses for local state.
/// Centralised so commands and handlers agree on a single config directory.
/// </summary>
public static class MdlPaths
{
    /// <summary>The MDL config directory, <c>~/.mdl</c> (or <c>./.mdl</c> when no home is available).</summary>
    public static string ConfigDirectory
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            return string.IsNullOrWhiteSpace(home)
                ? Path.Combine(Environment.CurrentDirectory, ".mdl")
                : Path.Combine(home, ".mdl");
        }
    }

    /// <summary>The local preferences file, <c>~/.mdl/config.json</c>.</summary>
    public static string ConfigFile => Path.Combine(ConfigDirectory, "config.json");
}
