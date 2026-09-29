using System.Collections;
using Tomix.Core.Models;

namespace Tomix.Core.Properties;

/// <summary>
/// One property of a model object as it surfaces across every output format: <paramref name="JsonKey"/>
/// is the camelCase JSON contract key, <paramref name="Header"/> the PascalCase CSV/text/find label,
/// and <paramref name="Value"/> extracts and normalizes the value (safe when the object's
/// <see cref="ModelObject.Properties"/> bag is null). The flags mark which commands consume the
/// property beyond get/ls: <paramref name="Writable"/> mirrors the mutator's setter whitelist,
/// <paramref name="Searchable"/>/<paramref name="SearchScope"/> drive find's field enumeration, and
/// <paramref name="Diffable"/> adds it to diff's per-object comparison. <paramref name="Default"/>
/// names the engine default when it is not the empty value (see <see cref="IsDefault"/>).
/// </summary>
public sealed record PropertyDescriptor(
    string JsonKey,
    string Header,
    Func<ModelObject, object?> Value,
    bool Writable = false,
    bool Searchable = false,
    string? SearchScope = null,
    bool Diffable = false,
    object? Default = null)
{
    /// <summary>
    /// Whether <paramref name="value"/> is this property's default, i.e. nobody set it: the
    /// declared <see cref="Default"/> when there is one, otherwise the empty value — <c>""</c>,
    /// <c>false</c>, <c>0</c>, an empty list, or the engine's <c>Default</c> enum member.
    /// Human views use it to show what was authored and fold the rest away.
    /// </summary>
    public bool IsDefault(object? value)
    {
        // A value the provider never reported (null or "") is unset whatever the declared default.
        if (value is null or "")
            return true;

        if (Default is not null)
            return Default is string text
                ? value is string actual && string.Equals(actual, text, StringComparison.OrdinalIgnoreCase)
                : Equals(value, Default);

        return value switch
        {
            string text => string.Equals(text, "Default", StringComparison.OrdinalIgnoreCase),
            bool flag => !flag,
            int number => number == 0,
            ICollection items => items.Count == 0,
            IEnumerable items => !items.GetEnumerator().MoveNext(),
            _ => false
        };
    }
}
