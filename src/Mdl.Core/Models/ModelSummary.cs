namespace Mdl.Core.Models;

public sealed record ModelSummary(
    string Name,
    int CompatibilityLevel,
    int Tables,
    int Columns,
    int Measures,
    int Relationships,
    int Roles);
