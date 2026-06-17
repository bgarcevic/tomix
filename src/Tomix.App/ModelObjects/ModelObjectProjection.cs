using Tomix.Core.Models;

namespace Tomix.App.ModelObjects;

internal static class ModelObjectProjection
{
    public static IReadOnlyList<ModelObject> Flatten(ModelSnapshot snapshot)
    {
        var objects = new List<ModelObject>();

        void Walk(IEnumerable<ModelObject> nodes)
        {
            foreach (var node in nodes)
            {
                objects.Add(node);
                Walk(node.Children);
            }
        }

        Walk(snapshot.Objects);
        return objects;
    }

    public static IReadOnlyDictionary<string, object?> ToProperties(ModelObject obj)
    {
        var counts = obj.Children.GroupBy(c => c.Kind).ToDictionary(g => g.Key, g => g.Count());

        if (obj.Kind == ModelObjectKind.Table)
        {
            return new Dictionary<string, object?>
            {
                ["name"] = obj.Name,
                ["description"] = obj.Description ?? "",
                ["isHidden"] = obj.Hidden,
                ["dataCategory"] = obj.Property("DataCategory") ?? "",
                ["lineageTag"] = obj.Property("LineageTag") ?? "",
                ["columns"] = counts.GetValueOrDefault(ModelObjectKind.Column),
                ["measures"] = counts.GetValueOrDefault(ModelObjectKind.Measure),
                ["hierarchies"] = counts.GetValueOrDefault(ModelObjectKind.Hierarchy),
                ["partitions"] = counts.GetValueOrDefault(ModelObjectKind.Partition),
                ["refreshPolicy"] = null,
                ["defaultDetailRowsExpression"] = null
            };
        }

        if (obj.Kind == ModelObjectKind.Measure)
        {
            return new Dictionary<string, object?>
            {
                ["name"] = obj.Name,
                ["description"] = obj.Description ?? "",
                ["isHidden"] = obj.Hidden,
                ["expression"] = obj.Expression ?? "",
                ["formatString"] = obj.Property("FormatString") ?? "",
                ["displayFolder"] = obj.Property("DisplayFolder") ?? "",
                ["dataType"] = obj.Property("DataType") ?? "",
                ["detailRowsExpression"] = obj.Property("DetailRowsExpression") ?? "",
                ["formatStringExpression"] = obj.Property("FormatStringExpression") ?? "",
                ["kpi"] = obj.Property("KPI") ?? "",
                ["lineageTag"] = obj.Property("LineageTag") ?? ""
            };
        }

        if (obj.Kind == ModelObjectKind.Column)
        {
            return new Dictionary<string, object?>
            {
                ["name"] = obj.Name,
                ["description"] = obj.Description ?? "",
                ["sourceColumn"] = obj.SourceColumn ?? "",
                ["dataType"] = obj.Property("DataType") ?? "",
                ["isHidden"] = obj.Hidden,
                ["formatString"] = obj.Property("FormatString") ?? "",
                ["displayFolder"] = obj.Property("DisplayFolder") ?? "",
                ["sortByColumn"] = obj.Property("SortByColumn") ?? "",
                ["summarizeBy"] = obj.Property("SummarizeBy") ?? "",
                ["lineageTag"] = obj.Property("LineageTag") ?? ""
            };
        }

        if (obj.Kind == ModelObjectKind.Partition)
        {
            return new Dictionary<string, object?>
            {
                ["name"] = obj.Name,
                ["description"] = obj.Description ?? "",
                ["expression"] = obj.Expression ?? "",
                ["mode"] = obj.Detail ?? "",
                ["dataView"] = obj.Property("DataView") ?? "",
                ["queryGroup"] = obj.Property("QueryGroup") ?? ""
            };
        }

        return new Dictionary<string, object?>
        {
            ["name"] = obj.Name,
            ["description"] = obj.Description ?? "",
            ["isHidden"] = obj.Hidden,
            ["detail"] = obj.Detail,
            ["expression"] = obj.Expression
        };
    }

    public static string KindLabel(ModelObjectKind kind) => kind switch
    {
        ModelObjectKind.RoleMember => "RoleMember",
        _ => kind.ToString()
    };
}
