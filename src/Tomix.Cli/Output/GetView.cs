using Tomix.App.Get;
using Tomix.Core.Models;
using Tomix.Core.Properties;

namespace Tomix.Cli.Output;

/// <summary>One property line in the <c>get</c> text view.</summary>
/// <remarks><paramref name="ReadOnly"/> marks catalog properties <c>tx set</c> rejects; only --all rows carry it.</remarks>
internal sealed record GetViewRow(string Key, object? Value, bool IsDefault, bool ReadOnly = false);

/// <summary>
/// Spectre-free layout of the <c>get</c> text view. By default it keeps only what was authored:
/// properties whose value differs from the engine default, then annotations and translations.
/// Settable properties still at their default fold into <see cref="Unset"/> (their <c>tx set</c>
/// tokens), and read-only defaults are dropped. <c>--all</c> lists every property in catalog order.
/// JSON and CSV never go through this view: their schema stays the full catalog projection.
/// </summary>
internal sealed record GetView(
    string Title,
    string Kind,
    IReadOnlyList<GetViewRow> Properties,
    IReadOnlyList<GetViewRow> Annotations,
    IReadOnlyList<GetViewRow> Translations,
    IReadOnlyList<string> Unset)
{
    private const string AnnotationPrefix = "annotation:";
    private const string TranslationPrefix = "translation:";

    public static GetView Build(GetObjectResult result, bool all)
    {
        var descriptors = ModelPropertyCatalog.For(result.Object.Kind)
            .ToDictionary(d => d.JsonKey, StringComparer.Ordinal);
        var title = result.Object.Kind == ModelObjectKind.Model ? result.Object.Name : result.Path;

        var properties = new List<GetViewRow>();
        var annotations = new List<GetViewRow>();
        var translations = new List<GetViewRow>();
        var unset = new List<string>();

        foreach (var (key, value) in result.Properties)
        {
            if (key.StartsWith(AnnotationPrefix, StringComparison.Ordinal))
            {
                annotations.Add(new GetViewRow(key[AnnotationPrefix.Length..], value, IsDefault: false));
                continue;
            }

            if (key.StartsWith(TranslationPrefix, StringComparison.Ordinal))
            {
                translations.Add(new GetViewRow(key[TranslationPrefix.Length..], value, IsDefault: false));
                continue;
            }

            descriptors.TryGetValue(key, out var descriptor);
            var isDefault = descriptor?.IsDefault(value) ?? false;

            if (all)
            {
                properties.Add(new GetViewRow(key, value, isDefault, ReadOnly: descriptor is { Writable: false }));
                continue;
            }

            // The title already names the object; the name row only earns its place when it
            // says something the path does not (a relationship's endpoint-derived name).
            if (key == "name" && IsRedundantName(value, title))
                continue;

            if (!isDefault)
                properties.Add(new GetViewRow(key, value, IsDefault: false));
            else if (descriptor is { Writable: true })
                unset.Add(key);
        }

        return new GetView(
            title,
            result.Type,
            properties,
            annotations,
            translations,
            unset);
    }

    private static bool IsRedundantName(object? value, string title)
    {
        if (value is not string name)
            return true;

        var slash = title.LastIndexOf('/');
        var leaf = slash < 0 ? title : title[(slash + 1)..];
        return string.Equals(name, leaf.Trim('\''), StringComparison.Ordinal)
               || string.Equals(name, title, StringComparison.Ordinal);
    }
}
