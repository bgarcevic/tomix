using Tomix.Core.Configuration;
using Tomix.Core.Dax;

namespace Tomix.App.Format;

/// <summary>
/// The style <c>tx format</c> prints with: DAX separators (display only), DAX function spacing,
/// and long or short lines (DAX and M). Persisted under the <c>format.*</c> config keys; a flag
/// overrides its key, and an unset or unreadable key falls back to the default.
/// </summary>
public sealed record FormatStyle(DaxSeparators Separators, DaxFunctionSpacing FunctionSpacing, bool Long)
{
    public static readonly IReadOnlyList<string> SeparatorValues = ["us", "eu"];
    public static readonly IReadOnlyList<string> FunctionSpacingValues = ["space", "none"];
    public static readonly IReadOnlyList<string> LinesValues = ["short", "long"];

    public static FormatStyle Default { get; } = new(DaxSeparators.Us, DaxFunctionSpacing.Space, Long: false);

    /// <summary>The style the config sets, with each unset or invalid key at its default.</summary>
    public static FormatStyle FromConfig(IDictionary<string, string> config)
    {
        var style = Default;
        if (config.TryGetValue(ConfigKeys.FormatSeparators, out var separators) && TryParseSeparators(separators, out var parsedSeparators))
            style = style with { Separators = parsedSeparators };
        if (config.TryGetValue(ConfigKeys.FormatFunctionSpacing, out var spacing) && TryParseFunctionSpacing(spacing, out var parsedSpacing))
            style = style with { FunctionSpacing = parsedSpacing };
        if (config.TryGetValue(ConfigKeys.FormatLines, out var lines) && TryParseLines(lines, out var isLong))
            style = style with { Long = isLong };
        return style;
    }

    public static bool TryParseSeparators(string? value, out DaxSeparators separators)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "us":
                separators = DaxSeparators.Us;
                return true;
            case "eu":
                separators = DaxSeparators.Eu;
                return true;
            default:
                separators = DaxSeparators.Us;
                return false;
        }
    }

    public static bool TryParseFunctionSpacing(string? value, out DaxFunctionSpacing spacing)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "space":
                spacing = DaxFunctionSpacing.Space;
                return true;
            case "none":
                spacing = DaxFunctionSpacing.None;
                return true;
            default:
                spacing = DaxFunctionSpacing.Space;
                return false;
        }
    }

    public static bool TryParseLines(string? value, out bool isLong)
    {
        isLong = string.Equals(value?.Trim(), "long", StringComparison.OrdinalIgnoreCase);
        return isLong || string.Equals(value?.Trim(), "short", StringComparison.OrdinalIgnoreCase);
    }
}
