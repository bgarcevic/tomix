using System.Text.Json.Serialization;

namespace Tomix.Core.Models;

public sealed record ModelSummary(
    string Name,
    int CompatibilityLevel,
    int Tables,
    int Columns,
    int Measures,
    int Relationships,
    int Roles,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DatabaseName = null);
