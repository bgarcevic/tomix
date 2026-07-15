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
        new("defaultDetailRowsExpression", "DefaultDetailRowsExpression", _ => "")
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
        _ => Generic
    };

    /// <summary>Projects the object's full property set, ordered and keyed by JSON key.</summary>
    public static IReadOnlyDictionary<string, object?> Project(ModelObject obj)
    {
        var properties = new Dictionary<string, object?>();
        foreach (var descriptor in For(obj.Kind))
            properties[descriptor.JsonKey] = descriptor.Value(obj);
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
