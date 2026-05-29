namespace Mdl.Core.Configuration;

/// <summary>
/// The set of preferences MDL stores in local config. Config holds preferences, never secrets.
/// </summary>
public static class ConfigKeys
{
    public const string DefaultFormat = "defaultFormat";
    public const string NoColor = "noColor";
    public const string Telemetry = "telemetry";
    public const string ActiveProfile = "activeProfile";
    public const string HideWarnings = "hideWarnings";

    public static readonly IReadOnlyList<string> All =
    [
        DefaultFormat,
        NoColor,
        Telemetry,
        ActiveProfile,
        HideWarnings
    ];

    public static bool IsKnown(string key) => All.Contains(key);
}
