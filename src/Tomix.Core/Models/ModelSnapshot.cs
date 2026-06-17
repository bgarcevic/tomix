namespace Tomix.Core.Models;

/// <summary>
/// A provider-agnostic, navigable view of a model. <see cref="Objects"/> holds the top-level
/// nodes (tables plus the model-level collections: relationships, roles, perspectives, cultures);
/// each node exposes its children, so an object path can be resolved without any provider types.
/// </summary>
public sealed record ModelSnapshot(
    string Name,
    int CompatibilityLevel,
    IReadOnlyList<ModelObject> Objects,
    IReadOnlyDictionary<string, string>? Properties = null);
