namespace Tomix.Core.Models;

/// <summary>
/// The kinds of objects that can appear in a model snapshot and be selected by an object path.
/// </summary>
public enum ModelObjectKind
{
    Table,
    Measure,
    Column,
    Hierarchy,
    Level,
    Partition,
    Relationship,
    Role,
    RoleMember,
    Perspective,
    Culture,
    CalculationItem,
    DataSource,
    Kpi,
    TablePermission,
    Calendar
}
