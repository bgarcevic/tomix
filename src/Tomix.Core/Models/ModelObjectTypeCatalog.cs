namespace Tomix.Core.Models;

/// <summary>
/// The single <c>--type</c> vocabulary. Every command that takes an object-kind <c>--type</c>
/// (ls, get, deps, set, mv, rm, format, find, replace) shares the discovery list — one canonical
/// token per <see cref="ModelObjectKind"/> — and <c>tx add</c> additionally accepts the creation
/// tokens for kinds the snapshot splits more finely than the selector (calc tables, calc groups,
/// partition flavors, data-source flavors). The parser and every help/error text derive from this
/// catalog, so the vocabularies cannot drift apart.
/// </summary>
public static class ModelObjectTypeCatalog
{
    public sealed record DiscoveryEntry(string Token, ModelObjectKind Kind, IReadOnlyList<string> Aliases);

    /// <summary>Discovery tokens in help-text order. Aliases parse but are never advertised.</summary>
    public static IReadOnlyList<DiscoveryEntry> Discovery { get; } =
    [
        new("table", ModelObjectKind.Table, []),
        new("measure", ModelObjectKind.Measure, []),
        new("column", ModelObjectKind.Column, []),
        new("calculatedcolumn", ModelObjectKind.CalculatedColumn, []),
        new("hierarchy", ModelObjectKind.Hierarchy, []),
        new("level", ModelObjectKind.Level, []),
        new("partition", ModelObjectKind.Partition, []),
        new("calculationitem", ModelObjectKind.CalculationItem, ["calcitem"]),
        new("member", ModelObjectKind.RoleMember, ["rolemember"]),
        new("relationship", ModelObjectKind.Relationship, []),
        new("role", ModelObjectKind.Role, []),
        new("perspective", ModelObjectKind.Perspective, []),
        new("culture", ModelObjectKind.Culture, []),
        new("datasource", ModelObjectKind.DataSource, []),
        new("kpi", ModelObjectKind.Kpi, []),
        new("tablepermission", ModelObjectKind.TablePermission, []),
        new("calendar", ModelObjectKind.Calendar, []),
        new("expression", ModelObjectKind.Expression, []),
        new("function", ModelObjectKind.Function, [])
    ];

    /// <summary>Creation display names for <c>tx add --type</c> help, in advertised order.</summary>
    public static IReadOnlyList<string> CreationDisplayNames { get; } =
    [
        "Table", "CalcTable", "CalcGroup", "Measure", "CalcColumn", "DataColumn",
        "Hierarchy", "Level", "Calendar", "CalcItem", "KPI", "Partition",
        "MPartition", "EntityPartition", "PolicyRangePartition", "Expression", "Function",
        "Perspective", "Culture", "ProviderDataSource", "StructuredDataSource",
        "Role", "TablePermission", "Member", "Relationship"
    ];

    /// <summary>Canonical discovery tokens, comma-joined for help text and the TOMIX_INVALID_TYPE hint.</summary>
    public static string DiscoveryListText { get; } = string.Join(", ", Discovery.Select(e => e.Token));

    /// <summary>Creation display names, comma-joined for the add help text.</summary>
    public static string CreationListText { get; } = string.Join(", ", CreationDisplayNames);

    /// <summary>Parses a discovery token or alias, case-insensitively, ignoring surrounding whitespace.</summary>
    public static bool TryParse(string? value, out ModelObjectKind kind)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        if (normalized is not null)
        {
            foreach (var entry in Discovery)
            {
                if (entry.Token == normalized || entry.Aliases.Contains(normalized))
                {
                    kind = entry.Kind;
                    return true;
                }
            }
        }

        kind = default;
        return false;
    }
}
