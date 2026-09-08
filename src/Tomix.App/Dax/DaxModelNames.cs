using Tomix.App.ModelObjects;
using Tomix.Core.Models;

namespace Tomix.App.Dax;

/// <summary>
/// Model metadata for output that colors DAX by resolving names, not just syntax: the engine's
/// classification cannot tell a measure reference from a column reference — both are bracketed
/// names, and the engine deliberately sees no model.
/// </summary>
public static class DaxModelNames
{
    /// <summary>Every measure name in the snapshot, case-insensitive like DAX name resolution.</summary>
    public static IReadOnlySet<string> MeasureNames(ModelSnapshot snapshot)
        => ModelObjectProjection.Flatten(snapshot)
            .Where(o => o.Kind == ModelObjectKind.Measure)
            .Select(o => o.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
