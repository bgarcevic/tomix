namespace Tomix.Core.Properties;

/// <summary>
/// Canonical keys of the <see cref="Models.ModelObject.Properties"/> string bag. Providers write
/// these keys; the property catalog and any other bag reader must reference them from here rather
/// than repeating the literals.
/// </summary>
public static class PropertyBagKeys
{
    public const string DataType = "DataType";
    public const string FormatString = "FormatString";
    public const string DisplayFolder = "DisplayFolder";
    public const string DataCategory = "DataCategory";
    public const string SortByColumn = "SortByColumn";
    public const string SummarizeBy = "SummarizeBy";
    public const string LineageTag = "LineageTag";
    public const string DetailRowsExpression = "DetailRowsExpression";
    public const string DefaultDetailRowsExpression = "DefaultDetailRowsExpression";
    public const string FormatStringExpression = "FormatStringExpression";
    public const string Kpi = "KPI";
    public const string KpiTargetExpression = "KpiTargetExpression";
    public const string KpiStatusExpression = "KpiStatusExpression";
    public const string KpiTrendExpression = "KpiTrendExpression";
    public const string KpiTargetFormatString = "KpiTargetFormatString";
    public const string FromColumn = "FromColumn";
    public const string ToColumn = "ToColumn";
    public const string FromCardinality = "FromCardinality";
    public const string ToCardinality = "ToCardinality";
    public const string CrossFilteringBehavior = "CrossFilteringBehavior";
    public const string IsActive = "IsActive";
    public const string RlsExpression = "RlsExpression";
    public const string DataView = "DataView";
    public const string QueryGroup = "QueryGroup";
    public const string RefreshPolicy = "RefreshPolicy";
    public const string AnnotationPrefix = "Annotation:";
}
