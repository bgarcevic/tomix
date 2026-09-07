namespace Tomix.Core.Properties;

/// <summary>
/// Canonical keys of the <see cref="Models.ModelObject.Properties"/> string bag. Providers write
/// these keys; the property catalog and any other bag reader must reference them from here rather
/// than repeating the literals.
/// </summary>
public static class PropertyBagKeys
{
    public const string DataType = "DataType";
    public const string ColumnType = "ColumnType";
    public const string FormatString = "FormatString";
    public const string DisplayFolder = "DisplayFolder";
    public const string DataCategory = "DataCategory";
    public const string SortByColumn = "SortByColumn";
    public const string SummarizeBy = "SummarizeBy";
    public const string LineageTag = "LineageTag";
    public const string SourceLineageTag = "SourceLineageTag";
    public const string IsKey = "IsKey";
    public const string IsNullable = "IsNullable";
    public const string IsUnique = "IsUnique";
    public const string IsAvailableInMDX = "IsAvailableInMDX";
    public const string KeepUniqueRows = "KeepUniqueRows";
    public const string EncodingHint = "EncodingHint";
    public const string Alignment = "Alignment";
    public const string TableDetailPosition = "TableDetailPosition";
    public const string Ordinal = "Ordinal";
    public const string IsDefaultLabel = "IsDefaultLabel";
    public const string IsDefaultImage = "IsDefaultImage";
    public const string DisplayOrdinal = "DisplayOrdinal";
    public const string SourceProviderType = "SourceProviderType";
    public const string IsDataTypeInferred = "IsDataTypeInferred";
    public const string IsSimpleMeasure = "IsSimpleMeasure";
    public const string DetailRowsExpression = "DetailRowsExpression";
    public const string DefaultDetailRowsExpression = "DefaultDetailRowsExpression";
    public const string FormatStringExpression = "FormatStringExpression";
    public const string Kpi = "KPI";
    public const string KpiTargetExpression = "KpiTargetExpression";
    public const string KpiStatusExpression = "KpiStatusExpression";
    public const string KpiTrendExpression = "KpiTrendExpression";
    public const string KpiTargetFormatString = "KpiTargetFormatString";
    public const string StatusGraphic = "StatusGraphic";
    public const string TrendGraphic = "TrendGraphic";
    public const string StatusDescription = "StatusDescription";
    public const string TargetDescription = "TargetDescription";
    public const string TrendDescription = "TrendDescription";
    public const string FromColumn = "FromColumn";
    public const string ToColumn = "ToColumn";
    public const string FromCardinality = "FromCardinality";
    public const string ToCardinality = "ToCardinality";
    public const string CrossFilteringBehavior = "CrossFilteringBehavior";
    public const string IsActive = "IsActive";
    public const string SecurityFilteringBehavior = "SecurityFilteringBehavior";
    public const string RelyOnReferentialIntegrity = "RelyOnReferentialIntegrity";
    public const string JoinOnDateBehavior = "JoinOnDateBehavior";
    public const string RlsExpression = "RlsExpression";
    public const string MemberId = "MemberId";
    public const string IdentityProvider = "IdentityProvider";
    public const string MemberType = "MemberType";
    public const string DataView = "DataView";
    public const string QueryGroup = "QueryGroup";
    public const string RetainDataTillForceCalculate = "RetainDataTillForceCalculate";
    public const string RefreshPolicy = "RefreshPolicy";
    public const string RefreshPolicySourceExpression = "RefreshPolicySourceExpression";
    public const string RefreshPolicyPollingExpression = "RefreshPolicyPollingExpression";
    public const string NoSelectionExpression = "NoSelectionExpression";
    public const string MultipleOrEmptySelectionExpression = "MultipleOrEmptySelectionExpression";
    public const string IsPrivate = "IsPrivate";
    public const string ExcludeFromModelRefresh = "ExcludeFromModelRefresh";
    public const string ExcludeFromAutomaticAggregations = "ExcludeFromAutomaticAggregations";
    public const string AlternateSourcePrecedence = "AlternateSourcePrecedence";
    public const string ShowAsVariationsOnly = "ShowAsVariationsOnly";
    public const string SystemManaged = "SystemManaged";
    public const string DirectLakeIndexingBehavior = "DirectLakeIndexingBehavior";
    public const string HideMembers = "HideMembers";
    public const string ExpressionKind = "ExpressionKind";
    public const string RemoteParameterName = "RemoteParameterName";
    public const string CompatibilityLevel = "CompatibilityLevel";
    public const string Culture = "Culture";
    public const string Collation = "Collation";
    public const string DiscourageImplicitMeasures = "DiscourageImplicitMeasures";
    public const string DiscourageCompositeModels = "DiscourageCompositeModels";
    public const string DiscourageReportMeasures = "DiscourageReportMeasures";
    public const string DefaultMode = "DefaultMode";
    public const string DefaultDataView = "DefaultDataView";
    public const string MaxParallelismPerQuery = "MaxParallelismPerQuery";
    public const string MaxParallelismPerRefresh = "MaxParallelismPerRefresh";
    public const string SourceQueryCulture = "SourceQueryCulture";
    public const string ForceUniqueNames = "ForceUniqueNames";
    public const string Precedence = "Precedence";
    public const string MaxConnections = "MaxConnections";
    public const string ImpersonationMode = "ImpersonationMode";
    public const string Isolation = "Isolation";
    public const string Timeout = "Timeout";
    public const string ContextExpression = "ContextExpression";
    public const string AnnotationPrefix = "Annotation:";
}
