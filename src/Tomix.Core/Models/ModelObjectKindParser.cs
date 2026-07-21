namespace Tomix.Core.Models;

public static class ModelObjectKindParser
{
    public static bool TryParse(string value, out ModelObjectKind kind)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "table": kind = ModelObjectKind.Table; return true;
            case "measure": kind = ModelObjectKind.Measure; return true;
            case "column":
            case "calculatedcolumn": kind = ModelObjectKind.Column; return true;
            case "hierarchy": kind = ModelObjectKind.Hierarchy; return true;
            case "level": kind = ModelObjectKind.Level; return true;
            case "partition": kind = ModelObjectKind.Partition; return true;
            case "calculationitem":
            case "calcitem": kind = ModelObjectKind.CalculationItem; return true;
            case "member":
            case "rolemember": kind = ModelObjectKind.RoleMember; return true;
            case "datasource": kind = ModelObjectKind.DataSource; return true;
            case "kpi": kind = ModelObjectKind.Kpi; return true;
            case "tablepermission": kind = ModelObjectKind.TablePermission; return true;
            case "calendar": kind = ModelObjectKind.Calendar; return true;
            case "relationship": kind = ModelObjectKind.Relationship; return true;
            case "role": kind = ModelObjectKind.Role; return true;
            case "perspective": kind = ModelObjectKind.Perspective; return true;
            case "culture": kind = ModelObjectKind.Culture; return true;
            default: kind = default; return false;
        }
    }
}
