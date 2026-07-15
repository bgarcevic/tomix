using Tomix.Core.Models;

namespace Tomix.Core.Properties;

/// <summary>
/// One property of a model object as it surfaces across every output format: <paramref name="JsonKey"/>
/// is the camelCase JSON contract key, <paramref name="Header"/> the PascalCase CSV/text/find label,
/// and <paramref name="Value"/> extracts and normalizes the value (safe when the object's
/// <see cref="ModelObject.Properties"/> bag is null). The flags mark which commands consume the
/// property beyond get/ls: <paramref name="Writable"/> mirrors the mutator's setter whitelist,
/// <paramref name="Searchable"/>/<paramref name="SearchScope"/> drive find's field enumeration, and
/// <paramref name="Diffable"/> adds it to diff's per-object comparison.
/// </summary>
public sealed record PropertyDescriptor(
    string JsonKey,
    string Header,
    Func<ModelObject, object?> Value,
    bool Writable = false,
    bool Searchable = false,
    string? SearchScope = null,
    bool Diffable = false);
