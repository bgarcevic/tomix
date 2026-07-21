using Tomix.Core.Models;

namespace Tomix.Core.Properties;

/// <summary>
/// The single catalog of model-object properties. Every command that surfaces properties
/// (get, ls, find, diff, set/add error hints) consumes these descriptors, so a property added
/// here appears in JSON, CSV, and text output everywhere at once and the formats cannot drift.
/// Conventions: absent string values project as <c>""</c> (never null); data types are
/// canonicalized via <see cref="NormalizeDataType"/> with <c>Unknown</c> treated as absent.
/// </summary>
public static class ModelPropertyCatalog
{
    private const string NamesScope = "names";
    private const string ExpressionsScope = "expressions";
    private const string DescriptionsScope = "descriptions";
    private const string FormatStringsScope = "formatstrings";
    private const string DisplayFoldersScope = "displayfolders";

    private static readonly IReadOnlyList<PropertyDescriptor> Table =
    [
        Name(writable: true),
        Description(writable: true),
        IsHidden(writable: true),
        new("dataCategory", "DataCategory", o => Bag(o, PropertyBagKeys.DataCategory), Writable: true, Diffable: true),
        new("lineageTag", "LineageTag", o => Bag(o, PropertyBagKeys.LineageTag)),
        new("columns", "Columns", o => Count(o, ModelObjectKind.Column)),
        new("measures", "Measures", o => Count(o, ModelObjectKind.Measure)),
        new("hierarchies", "Hierarchies", o => Count(o, ModelObjectKind.Hierarchy)),
        new("partitions", "Partitions", o => Count(o, ModelObjectKind.Partition)),
        new("refreshPolicy", "RefreshPolicy", o => Bag(o, PropertyBagKeys.RefreshPolicy)),
        new("defaultDetailRowsExpression", "DefaultDetailRowsExpression", o => Bag(o, PropertyBagKeys.DefaultDetailRowsExpression), Diffable: true)
    ];

    private static readonly IReadOnlyList<PropertyDescriptor> Measure =
    [
        Name(writable: true),
        Description(writable: true),
        IsHidden(writable: true),
        Expression(writable: true),
        FormatString(),
        DisplayFolder(),
        DataType(diffable: true),
        new("detailRowsExpression", "DetailRowsExpression", o => Bag(o, PropertyBagKeys.DetailRowsExpression), Diffable: true),
        new("formatStringExpression", "FormatStringExpression", o => Bag(o, PropertyBagKeys.FormatStringExpression), Diffable: true),
        new("kpi", "KPI", o => Bag(o, PropertyBagKeys.Kpi), Diffable: true),
        new("kpiTargetExpression", "KpiTargetExpression", o => Bag(o, PropertyBagKeys.KpiTargetExpression), Diffable: true),
        new("kpiStatusExpression", "KpiStatusExpression", o => Bag(o, PropertyBagKeys.KpiStatusExpression), Diffable: true),
        new("kpiTrendExpression", "KpiTrendExpression", o => Bag(o, PropertyBagKeys.KpiTrendExpression), Diffable: true),
        new("lineageTag", "LineageTag", o => Bag(o, PropertyBagKeys.LineageTag))
    ];

    private static readonly IReadOnlyList<PropertyDescriptor> Column =
    [
        Name(writable: true),
        Description(writable: true),
        new("sourceColumn", "SourceColumn", o => o.SourceColumn ?? ""),
        Expression(writable: false),
        // Not diffable: a column's Detail IS its data type, and diff already compares Detail
        // in its fixed identity set — marking this diffable would report the same edit twice.
        DataType(diffable: false),
        IsHidden(writable: true),
        FormatString(),
        DisplayFolder(),
        new("sortByColumn", "SortByColumn", o => Bag(o, PropertyBagKeys.SortByColumn), Diffable: true),
        new("summarizeBy", "SummarizeBy", o => Bag(o, PropertyBagKeys.SummarizeBy), Diffable: true),
        new("lineageTag", "LineageTag", o => Bag(o, PropertyBagKeys.LineageTag))
    ];

    private static readonly IReadOnlyList<PropertyDescriptor> Partition =
    [
        Name(writable: true),
        Description(writable: false),
        Expression(writable: true),
        new("mode", "Mode", o => o.Detail ?? ""),
        new("dataView", "DataView", o => Bag(o, PropertyBagKeys.DataView), Diffable: true),
        new("queryGroup", "QueryGroup", o => Bag(o, PropertyBagKeys.QueryGroup), Diffable: true)
    ];

    private static readonly IReadOnlyList<PropertyDescriptor> Relationship =
    [
        Name(writable: false),
        // Endpoints, cardinality, and active state are also encoded in the relationship's Detail
        // string, which diff already compares in its fixed identity set — only properties absent
        // from Detail are diffable here, so an edit is never reported twice.
        new("fromColumn", "FromColumn", o => Bag(o, PropertyBagKeys.FromColumn)),
        new("toColumn", "ToColumn", o => Bag(o, PropertyBagKeys.ToColumn)),
        new("fromCardinality", "FromCardinality", o => Bag(o, PropertyBagKeys.FromCardinality)),
        new("toCardinality", "ToCardinality", o => Bag(o, PropertyBagKeys.ToCardinality)),
        new("crossFilteringBehavior", "CrossFilteringBehavior", o => Bag(o, PropertyBagKeys.CrossFilteringBehavior), Diffable: true),
        new("isActive", "IsActive", o => Bag(o, PropertyBagKeys.IsActive) == "true")
    ];

    private static readonly IReadOnlyList<PropertyDescriptor> Role =
    [
        Name(writable: false),
        Description(writable: false),
        // ModelPermission is the role's Detail, which diff already compares — not diffable here.
        new("modelPermission", "ModelPermission", o => o.Detail ?? ""),
        new("rlsExpression", "RlsExpression", o => Bag(o, PropertyBagKeys.RlsExpression), Diffable: true),
        new("members", "Members", o => Count(o, ModelObjectKind.RoleMember))
    ];

    private static readonly IReadOnlyList<PropertyDescriptor> Kpi =
    [
        Name(writable: false),
        Description(writable: true),
        // KPI expressions are diffed via the parent measure's kpi* properties — not diffable here.
        new("targetExpression", "TargetExpression", o => Bag(o, PropertyBagKeys.KpiTargetExpression), Writable: true),
        new("statusExpression", "StatusExpression", o => Bag(o, PropertyBagKeys.KpiStatusExpression), Writable: true),
        new("trendExpression", "TrendExpression", o => Bag(o, PropertyBagKeys.KpiTrendExpression), Writable: true),
        // Only surfaced here (the measure's kpi* properties omit it), so it is diffable.
        new("targetFormatString", "TargetFormatString", o => Bag(o, PropertyBagKeys.KpiTargetFormatString), Writable: true, Diffable: true)
    ];

    private static readonly IReadOnlyList<PropertyDescriptor> TablePermission =
    [
        Name(writable: true),
        // MetadataPermission is the permission's Detail, which diff already compares — not diffable here.
        new("metadataPermission", "MetadataPermission", o => o.Detail ?? ""),
        // The RLS filter is diffed via the parent role's rlsExpression — not diffable here.
        new("filterExpression", "FilterExpression", o => o.Expression ?? "", Writable: true)
    ];

    private static readonly IReadOnlyList<PropertyDescriptor> Generic =
    [
        Name(writable: false),
        Description(writable: false),
        IsHidden(writable: false),
        new("detail", "Detail", o => o.Detail ?? ""),
        Expression(writable: false)
    ];

    /// <summary>The fallback descriptors every kind can answer; used for mixed-kind listings.</summary>
    public static IReadOnlyList<PropertyDescriptor> GenericDescriptors => Generic;

    public static IReadOnlyList<PropertyDescriptor> For(ModelObjectKind kind) => kind switch
    {
        ModelObjectKind.Table => Table,
        ModelObjectKind.Measure => Measure,
        ModelObjectKind.Column => Column,
        ModelObjectKind.Partition => Partition,
        ModelObjectKind.Relationship => Relationship,
        ModelObjectKind.Role => Role,
        ModelObjectKind.Kpi => Kpi,
        ModelObjectKind.TablePermission => TablePermission,
        _ => Generic
    };

    /// <summary>
    /// Projects the object's full property set, ordered and keyed by JSON key. Annotations from
    /// the property bag are appended after the descriptors as <c>annotation:&lt;name&gt;</c>
    /// entries (name-ordered); they appear in JSON and text output, while CSV stays limited to
    /// the fixed descriptor columns.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> Project(ModelObject obj)
    {
        var properties = new Dictionary<string, object?>();
        foreach (var descriptor in For(obj.Kind))
            properties[descriptor.JsonKey] = descriptor.Value(obj);

        if (obj.Properties is null)
            return properties;

        foreach (var (key, value) in obj.Properties
                     .Where(p => p.Key.StartsWith(PropertyBagKeys.AnnotationPrefix, StringComparison.Ordinal))
                     .OrderBy(p => p.Key, StringComparer.Ordinal))
            properties[$"annotation:{key[PropertyBagKeys.AnnotationPrefix.Length..]}"] = value;

        return properties;
    }

    /// <summary>The find/replace scope tokens, in the order find documents them.</summary>
    public static IReadOnlyList<string> SearchScopes { get; } =
        [NamesScope, ExpressionsScope, DescriptionsScope, FormatStringsScope, DisplayFoldersScope];

    /// <summary>
    /// JSON keys of the properties the mutator can set on this kind, for error hints. Empty for
    /// kinds whose writable set the catalog does not model (the mutator stays the authority).
    /// </summary>
    public static IReadOnlyList<string> WritableTokens(ModelObjectKind kind) => kind switch
    {
        ModelObjectKind.Table or ModelObjectKind.Measure or ModelObjectKind.Column or ModelObjectKind.Partition
            => For(kind).Where(d => d.Writable).Select(d => d.JsonKey).ToList(),
        _ => []
    };

    /// <summary>Canonicalizes a data-type name; <c>Unknown</c> and empty mean "absent" and map to <c>""</c>.</summary>
    public static string NormalizeDataType(string? raw)
        => raw?.Trim().ToLowerInvariant() switch
        {
            null or "" or "unknown" => "",
            "int64" => "Int64",
            "decimal" => "Decimal",
            "double" => "Double",
            "string" => "String",
            "boolean" or "bool" => "Boolean",
            "datetime" => "DateTime",
            _ => raw.Trim()
        };

    private static PropertyDescriptor Name(bool writable)
        => new("name", "Name", o => o.Name, Writable: writable, Searchable: true, SearchScope: NamesScope);

    private static PropertyDescriptor Description(bool writable)
        => new("description", "Description", o => o.Description ?? "", Writable: writable, Searchable: true, SearchScope: DescriptionsScope);

    private static PropertyDescriptor IsHidden(bool writable)
        => new("isHidden", "Hidden", o => o.Hidden, Writable: writable);

    private static PropertyDescriptor Expression(bool writable)
        => new("expression", "Expression", o => o.Expression ?? "", Writable: writable, Searchable: true, SearchScope: ExpressionsScope);

    private static PropertyDescriptor FormatString()
        => new("formatString", "FormatString", o => Bag(o, PropertyBagKeys.FormatString), Writable: true, Searchable: true, SearchScope: FormatStringsScope, Diffable: true);

    private static PropertyDescriptor DisplayFolder()
        => new("displayFolder", "DisplayFolder", o => Bag(o, PropertyBagKeys.DisplayFolder), Writable: true, Searchable: true, SearchScope: DisplayFoldersScope, Diffable: true);

    private static PropertyDescriptor DataType(bool diffable)
        => new("dataType", "DataType", o => NormalizeDataType(o.Property(PropertyBagKeys.DataType) ?? o.Detail), Diffable: diffable);

    private static string Bag(ModelObject obj, string key)
        => obj.Property(key) ?? "";

    private static int Count(ModelObject obj, ModelObjectKind kind)
        => obj.Children.Count(c => c.Kind == kind);
}
