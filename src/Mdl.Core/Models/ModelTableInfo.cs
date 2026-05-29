namespace Mdl.Core.Models;

public sealed record ModelTableInfo(
    string Name,
    int Columns,
    int Measures,
    bool Hidden,
    bool Calculated);
