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
            case "partition": kind = ModelObjectKind.Partition; return true;
            case "relationship": kind = ModelObjectKind.Relationship; return true;
            case "role": kind = ModelObjectKind.Role; return true;
            case "perspective": kind = ModelObjectKind.Perspective; return true;
            case "culture": kind = ModelObjectKind.Culture; return true;
            default: kind = default; return false;
        }
    }
}
