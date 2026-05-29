namespace Mdl.Core.Models;

public sealed record ModelInventory(
    string Name,
    int CompatibilityLevel,
    int Tables,
    int Columns,
    int Measures,
    int Relationships,
    int Roles,
    int CalculationGroups,
    IReadOnlyList<ModelTableInfo> TableDetails);
