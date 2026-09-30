using System.Globalization;
using System.Text.RegularExpressions;

namespace Tomix.App.Get;

/// <summary>
/// One <c>--where Prop=Value</c> filter. Matching is case-insensitive on both sides, and
/// <c>*</c> is the only wildcard: <c>*margin*</c> contains, <c>margin*</c> starts with,
/// <c>*margin</c> ends with, and a value with no <c>*</c> must equal the whole property value.
/// </summary>
public sealed class PropertyFilter
{
    private readonly Regex _pattern;

    private PropertyFilter(string property, string value)
    {
        Property = property;
        Value = value;
        var body = string.Join(".*", value.Split('*').Select(Regex.Escape));
        _pattern = new Regex($"^{body}$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }

    public string Property { get; }

    public string Value { get; }

    /// <summary>Parses <c>Prop=Value</c>, splitting at the first <c>=</c>; the value may be empty.</summary>
    public static bool TryParse(string? text, out PropertyFilter filter)
    {
        filter = null!;
        var separator = text?.IndexOf('=') ?? -1;
        if (separator <= 0)
            return false;

        var property = text![..separator].Trim();
        if (property.Length == 0)
            return false;

        filter = new PropertyFilter(property, text[(separator + 1)..]);
        return true;
    }

    /// <summary>
    /// Whether a projected property bag passes. An object that lacks the property never does:
    /// in a mixed list, <c>DataType=String</c> keeps string columns and drops the measures.
    /// </summary>
    public bool Matches(IReadOnlyDictionary<string, object?> properties)
    {
        foreach (var (key, value) in properties)
        {
            if (string.Equals(key, Property, StringComparison.OrdinalIgnoreCase))
                return Text(value) is { } text && _pattern.IsMatch(text);
        }

        return false;
    }

    /// <summary>Whether any bag carries the property at all, so a typo can be told from no match.</summary>
    public bool IsKnownIn(IReadOnlyDictionary<string, object?> properties)
        => properties.Keys.Any(key => string.Equals(key, Property, StringComparison.OrdinalIgnoreCase));

    private static string? Text(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        System.Collections.IEnumerable => null,
        _ => value.ToString()
    };
}
