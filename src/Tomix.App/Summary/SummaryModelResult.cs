using System.Text.Json.Serialization;

namespace Tomix.App.Summary;

public sealed record SummaryModelResult(
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Database,
    string Source,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Format,
    int CompatibilityLevel,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Culture,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DefaultMode,
    SummaryCounts Counts);

public sealed record SummaryCounts(
    int Tables,
    int Columns,
    int Measures,
    int Relationships,
    int Roles,
    int Partitions,
    int CalculationGroups,
    int Perspectives,
    int Cultures);
