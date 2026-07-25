using Tomix.Core.Models;

namespace Tomix.App.Bpa.Model;

/// <summary>
/// Adapter object model that mirrors the TOM-style metadata surface that BPA rule
/// <c>Expression</c> strings are written against, so a rule's expression can be evaluated
/// directly (via <see cref="BpaExpressionEvaluator"/>) instead of being re-implemented in C#.
///
/// These are plain CLR objects built from a provider-agnostic <see cref="ModelSnapshot"/> by
/// <see cref="BpaModelBuilder"/>. Only members that can be computed accurately from the snapshot
/// are exposed; an expression that references a member we cannot populate simply fails to bind
/// and that rule is skipped (never a false positive).
/// </summary>
public abstract class BpaObject
{
    /// <summary>The originating snapshot object — used to build the violation, not by expressions.</summary>
    public ModelObject Source { get; internal set; } = null!;

    /// <summary>Back-reference so expressions can navigate <c>Model.Tables</c>, <c>Model.AllMeasures</c>, …</summary>
    public BpaModel Model { get; internal set; } = null!;

    public string Name { get; internal set; } = "";

    public string Description { get; internal set; } = "";

    public bool IsHidden { get; internal set; }

    /// <summary>
    /// Reads an annotation value off the originating snapshot object. Returns null when the
    /// annotation is absent — matching the TOM-style semantics rule expressions are written
    /// against, where e.g. <c>Convert.ToInt64(GetAnnotation("Vertipaq_RowCount"))</c> yields 0
    /// for a model that has no such annotation instead of throwing on an empty string.
    /// </summary>
    public string? GetAnnotation(string name)
        => Source.Property($"Annotation:{name}");
}

public sealed class BpaColumn : BpaObject
{
    public string DataType { get; internal set; } = "";
    public bool IsKey { get; internal set; }
    public bool IsAvailableInMDX { get; internal set; }
    public string FormatString { get; internal set; } = "";
    public string DataCategory { get; internal set; } = "";
    public string SummarizeBy { get; internal set; } = "";
    public string SourceColumn { get; internal set; } = "";
    public string? SortByColumn { get; internal set; }

    /// <summary>"Data", "Calculated", or "CalculatedTableColumn".</summary>
    public string ColumnType { get; internal set; } = "";

    /// <summary>Alias of <see cref="ColumnType"/>; some rules use <c>Type.ToString()</c>.</summary>
    public string Type => ColumnType;

    /// <summary>The DAX expression for a calculated column (empty for data columns).</summary>
    public string Expression => Source.Expression ?? "";

    public BpaTable Table { get; internal set; } = null!;

    public IReadOnlyList<BpaDependsOnEntry> DependsOn { get; internal set; } = [];

    /// <summary>Relationships in which this column participates.</summary>
    public IReadOnlyList<BpaRelationship> UsedInRelationships { get; internal set; } = [];

    public BpaReferencedBy ReferencedBy { get; internal set; } = BpaReferencedBy.Empty;

    /// <summary>Columns that sort BY this column (inverse of <see cref="SortByColumn"/>).</summary>
    public IReadOnlyList<BpaColumn> UsedInSortBy { get; internal set; } = [];

    /// <summary>Names of hierarchies whose levels reference this column.</summary>
    public IReadOnlyList<string> UsedInHierarchies { get; internal set; } = [];

    /// <summary>Names of variations defined on this column.</summary>
    public IReadOnlyList<string> UsedInVariations { get; internal set; } = [];

    /// <summary>Object-level-security entries referencing this column (not captured from a static file; empty).</summary>
    public IReadOnlyList<string> ObjectLevelSecurity { get; internal set; } = [];

    /// <summary>Non-null when this column is an alternate-of (aggregation) column.</summary>
    public string? AlternateOf { get; internal set; }
}

public sealed class BpaMeasure : BpaObject
{
    public string DataType { get; internal set; } = "";
    public string FormatString { get; internal set; } = "";

    /// <summary>The dynamic format-string DAX expression (empty when the measure has none).</summary>
    public string FormatStringExpression { get; internal set; } = "";

    public string Expression { get; internal set; } = "";

    /// <summary>The unqualified DAX name, e.g. <c>[Sales Amount]</c>.</summary>
    public string DaxObjectName { get; internal set; } = "";

    public BpaTable Table { get; internal set; } = null!;

    public IReadOnlyList<BpaDependsOnEntry> DependsOn { get; internal set; } = [];

    public BpaReferencedBy ReferencedBy { get; internal set; } = BpaReferencedBy.Empty;
}

public sealed class BpaTable : BpaObject
{
    public string DataCategory { get; internal set; } = "";

    /// <summary>"Table", "Calculated Table", or "Table (DirectQuery)".</summary>
    public string ObjectTypeName { get; internal set; } = "Table";

    public IReadOnlyList<BpaColumn> Columns { get; internal set; } = [];
    public IReadOnlyList<BpaPartition> Partitions { get; internal set; } = [];

    /// <summary>DAX dependencies of a calculated table (derived from its expression; empty otherwise).</summary>
    public IReadOnlyList<BpaDependsOnEntry> DependsOn { get; internal set; } = [];
    public IReadOnlyList<BpaRelationship> UsedInRelationships { get; internal set; } = [];

    /// <summary>RLS filter expressions defined on this table across roles.</summary>
    public IReadOnlyList<string> RowLevelSecurity { get; internal set; } = [];

    /// <summary>Object-level-security entries on this table (not captured from a static file; empty).</summary>
    public IReadOnlyList<string> ObjectLevelSecurity { get; internal set; } = [];

    /// <summary>Calculation items, when this table is a calculation group.</summary>
    public IReadOnlyList<BpaCalculationItem> CalculationItems { get; internal set; } = [];

    /// <summary>Membership keyed by perspective name (every perspective is present; value is membership).</summary>
    public IReadOnlyDictionary<string, bool> InPerspective { get; internal set; }
        = new Dictionary<string, bool>();

    /// <summary>Source/named-expression text (not captured from a static file; empty).</summary>
    public string SourceExpression { get; internal set; } = "";
}

public sealed class BpaRelationship : BpaObject
{
    // Endpoints resolved to the actual objects so expressions can navigate
    // FromColumn.DataType, FromTable.Name, etc. Null for unresolved/multi-column relationships.
    public BpaTable? FromTable { get; internal set; }
    public BpaTable? ToTable { get; internal set; }
    public BpaColumn? FromColumn { get; internal set; }
    public BpaColumn? ToColumn { get; internal set; }
    public string FromCardinality { get; internal set; } = "";
    public string ToCardinality { get; internal set; } = "";
    public string CrossFilteringBehavior { get; internal set; } = "";
    public bool IsActive { get; internal set; }
}

public sealed class BpaPartition : BpaObject
{
    public string SourceType { get; internal set; } = "";
    public string Query { get; internal set; } = "";
    public string Mode { get; internal set; } = "";
    public BpaDataSourceRef DataSource { get; internal set; } = new();
}

public sealed class BpaRole : BpaObject
{
    /// <summary>Per-table RLS filter expressions defined on this role.</summary>
    public IReadOnlyList<string> RowLevelSecurity { get; internal set; } = [];

    /// <summary>Member names of this role.</summary>
    public IReadOnlyList<string> Members { get; internal set; } = [];
}

public sealed class BpaHierarchy : BpaObject
{
}

/// <summary>A calculation item within a calculation group (DAX-bearing, like a measure).</summary>
public sealed class BpaCalculationItem : BpaObject
{
    public string Expression { get; internal set; } = "";
    public IReadOnlyList<BpaDependsOnEntry> DependsOn { get; internal set; } = [];
}

public sealed class BpaDataSource : BpaObject
{
    /// <summary>"Structured" or "Provider".</summary>
    public string Type { get; internal set; } = "";
    public IReadOnlyList<BpaPartition> UsedByPartitions { get; internal set; } = [];
}

public sealed class BpaPerspective : BpaObject
{
}

/// <summary>Lightweight reference to a partition's data source (for <c>DataSource.Type</c>).</summary>
public sealed class BpaDataSourceRef
{
    public string Name { get; internal set; } = "";
    public string Type { get; internal set; } = "";
}

/// <summary>The whole model — the <c>it</c> for model-scoped rules and the target of <c>Model.*</c>.</summary>
public sealed class BpaModel
{
    public string Name { get; internal set; } = "";
    public IReadOnlyList<BpaTable> Tables { get; internal set; } = [];
    public IReadOnlyList<BpaMeasure> AllMeasures { get; internal set; } = [];
    public IReadOnlyList<BpaColumn> AllColumns { get; internal set; } = [];
    public IReadOnlyList<BpaPartition> AllPartitions { get; internal set; } = [];
    public IReadOnlyList<BpaRole> Roles { get; internal set; } = [];
    public IReadOnlyList<BpaRelationship> Relationships { get; internal set; } = [];
    public IReadOnlyList<BpaCalculationItem> AllCalculationItems { get; internal set; } = [];
    public IReadOnlyList<BpaDataSource> DataSources { get; internal set; } = [];
    public IReadOnlyList<BpaPerspective> Perspectives { get; internal set; } = [];
    public IReadOnlyList<BpaHierarchy> Hierarchies { get; internal set; } = [];
    public string DefaultPowerBIDataSourceVersion { get; internal set; } = "";

    /// <summary>Synthetic snapshot object used to build a model-scoped violation.</summary>
    public ModelObject Source { get; internal set; } = null!;
}

/// <summary>
/// One grouped dependency of an object: a referenced object-type (<c>Key.ObjectType</c>) and the
/// individual references of that type (<c>Value</c>), each carrying whether it was written
/// fully-qualified (<c>'Table'[Name]</c>) or not (<c>[Name]</c>).
/// Shapes the <c>DependsOn.Any(Key.ObjectType = "Measure" and Value.Any(FullyQualified))</c> idiom.
/// </summary>
public sealed class BpaDependsOnEntry
{
    public BpaDependsOnKey Key { get; internal set; } = new();
    public IReadOnlyList<BpaReference> Value { get; internal set; } = [];
}

public sealed class BpaDependsOnKey
{
    /// <summary>"Measure" or "Column".</summary>
    public string ObjectType { get; internal set; } = "";
}

public sealed class BpaReference
{
    public bool FullyQualified { get; internal set; }
    public string ObjectType { get; internal set; } = "";
    public string Name { get; internal set; } = "";

    /// <summary>The table qualifier when the reference was written <c>'Table'[X]</c>, else null.</summary>
    public string? Table { get; internal set; }
}

/// <summary>Inverse of <see cref="BpaObject"/> dependencies: who references this object.</summary>
public sealed class BpaReferencedBy
{
    public static readonly BpaReferencedBy Empty = new() { AllMeasures = [] };

    public IReadOnlyList<BpaMeasure> AllMeasures { get; internal set; } = [];

    /// <summary>Total number of objects (measures + columns) that reference this object.</summary>
    public int Count { get; internal set; }
}
