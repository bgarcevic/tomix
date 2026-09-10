namespace Tomix.App.Format;

public static class FormatterLanguages
{
    public const string Dax = "dax";
    public const string PowerQuery = "powerquery";

    /// <summary>
    /// Whether <paramref name="language"/> names DAX. Accepts the canonical name and the
    /// display name (<see cref="DisplayName"/>), so renderers can gate without a string literal.
    /// </summary>
    public static bool IsDax(string language)
        => string.Equals(language, Dax, StringComparison.OrdinalIgnoreCase);

    public static bool TryNormalize(string? value, out string language)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            language = Dax;
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "dax":
                language = Dax;
                return true;
            case "m":
            case "pq":
            case "powerquery":
            case "power-query":
            case "power query":
                language = PowerQuery;
                return true;
            default:
                language = value.Trim().ToLowerInvariant();
                return false;
        }
    }

    public static string DisplayName(string language)
        => language == PowerQuery ? "m" : Dax;
}
