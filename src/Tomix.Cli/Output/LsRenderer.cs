using System.Text;
using System.Text.RegularExpressions;
using Tomix.App.Ls;
using Tomix.Core.Models;
using Spectre.Console;

namespace Tomix.Cli.Output;

internal sealed partial class LsRenderer
{
    private const int MaxCollapsedDetail = 80;
    private const int MeasureExpressionPreviewLines = 3;

    public static void Render(LsModelResult data, bool pathsOnly, bool noMultiline)
    {
        if (data.Objects.Count == 0)
        {
            if (pathsOnly)
                return;

            AnsiConsole.MarkupLine(Styling.Muted("No objects found."));
            AnsiConsole.MarkupLine(Styling.Guidance("  → Try: tx ls, tx ls --type table, or tx ls \"Sa*\""));
            return;
        }

        if (pathsOnly)
        {
            foreach (var obj in data.Objects)
                AnsiConsole.WriteLine(obj.Path);

            return;
        }

        var allTables = data.Objects.Count > 0 && data.Objects.All(o => o.Kind == ModelObjectKind.Table);

        if (allTables)
        {
            AnsiConsole.MarkupLine(Styling.Title($"Tables ({data.Objects.Count})"));
            RenderTables(data.Objects);
        }
        else
        {
            RenderGrouped(data.Objects, noMultiline);
        }
    }

    private static void RenderTables(IReadOnlyList<LsObject> objects)
    {
        var table = NewTable("Name", "Columns", "Measures", "Partitions", "Hidden", "Description");

        foreach (var o in objects)
        {
            table.AddRow(
                Styling.MarkupEscape(o.Name),
                Count(o, ModelObjectKind.Column),
                Count(o, ModelObjectKind.Measure),
                Count(o, ModelObjectKind.Partition),
                BoolText(o.Hidden),
                Styling.MarkupEscape(o.Description ?? ""));
        }

        AnsiConsole.Write(table);
    }

    private static void RenderGrouped(IReadOnlyList<LsObject> objects, bool noMultiline)
    {
        var groups = objects
            .GroupBy(o => o.Kind)
            .OrderBy(g => KindOrder(g.Key))
            .Select(g => (Kind: g.Key, Items: (IReadOnlyList<LsObject>)g.ToList()))
            .ToList();

        var first = true;
        foreach (var (kind, items) in groups)
        {
            if (!first)
                AnsiConsole.WriteLine();
            first = false;

            AnsiConsole.MarkupLine(Styling.Title($"{KindPlural(kind)} ({items.Count})"));

            switch (kind)
            {
                case ModelObjectKind.Column:
                    RenderColumns(items);
                    break;
                case ModelObjectKind.Measure:
                    RenderMeasures(items, noMultiline);
                    break;
                case ModelObjectKind.Hierarchy:
                    RenderHierarchies(items);
                    break;
                case ModelObjectKind.Partition:
                    RenderPartitions(items, noMultiline);
                    break;
                case ModelObjectKind.Level:
                    RenderLevels(items);
                    break;
                default:
                    RenderGeneric(items, noMultiline);
                    break;
            }
        }
    }

    private static void RenderColumns(IReadOnlyList<LsObject> objects)
    {
        var table = NewTable("Name", "SourceColumn", "DataType", "Description", "Hidden");

        foreach (var o in objects)
        {
            table.AddRow(
                Styling.MarkupEscape(o.Name),
                Styling.MarkupEscape(o.SourceColumn ?? ""),
                Styling.MarkupEscape(ColumnDataTypeDisplay(o)),
                Styling.MarkupEscape(o.Description ?? ""),
                BoolText(o.Hidden));
        }

        AnsiConsole.Write(table);
    }

    private static void RenderMeasures(IReadOnlyList<LsObject> objects, bool noMultiline)
    {
        var table = NewTable("Name", "Description", "Hidden", "Expression", "FormatString");

        foreach (var o in objects)
        {
            var lines = ExpressionLines(o, noMultiline);
            var hidden = lines.Count - MeasureExpressionPreviewLines;
            var expression = hidden > 0
                ? string.Join("\n", lines.Take(MeasureExpressionPreviewLines))
                  + $"\n... (+{hidden} {(hidden == 1 ? "line" : "lines")})"
                : string.Join("\n", lines);
            table.AddRow(
                Styling.MarkupEscape(o.Name),
                Styling.MarkupEscape(o.Description ?? ""),
                BoolText(o.Hidden),
                Styling.MarkupEscape(expression),
                Styling.MarkupEscape(Projected(o, "formatString")));
        }

        AnsiConsole.Write(table);
    }

    private static void RenderHierarchies(IReadOnlyList<LsObject> objects)
    {
        var table = NewTable("Name", "Levels", "Hidden");
        var showDescription = objects.Any(o => !string.IsNullOrEmpty(o.Description));
        if (showDescription)
            table.AddColumn(new TableColumn("Description") { Alignment = Justify.Left });

        foreach (var obj in objects)
        {
            var rows = new List<string>
            {
                Styling.MarkupEscape(obj.Name),
                Count(obj, ModelObjectKind.Level),
                BoolText(obj.Hidden)
            };
            if (showDescription)
                rows.Add(Styling.MarkupEscape(obj.Description ?? ""));
            table.AddRow(rows.ToArray());
        }

        AnsiConsole.Write(table);
    }

    private static void RenderPartitions(IReadOnlyList<LsObject> objects, bool noMultiline)
    {
        var showDescription = objects.Any(o => !string.IsNullOrEmpty(o.Description));
        var showExpression = objects.Any(o => !string.IsNullOrEmpty(o.Expression ?? o.Detail));
        var exprLines = objects.ToDictionary(o => o, o => ExpressionLines(o, noMultiline));

        var table = new Table()
            .RoundedBorder()
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]Name[/]") { Alignment = Justify.Left })
            .AddColumn(new TableColumn("[bold]Mode[/]") { Alignment = Justify.Left });

        if (showExpression)
            table.AddColumn(new TableColumn("[bold]Expression[/]") { Alignment = Justify.Left });
        if (showDescription)
            table.AddColumn(new TableColumn("[bold]Description[/]") { Alignment = Justify.Left });

        foreach (var obj in objects)
        {
            var rows = new List<string>
            {
                Styling.MarkupEscape(obj.Name),
                Styling.MarkupEscape(obj.Detail ?? "")
            };

            if (showExpression)
            {
                var lines = exprLines[obj];
                rows.Add(Styling.MarkupEscape(string.Join("\n", lines)));
            }
            if (showDescription)
                rows.Add(Styling.MarkupEscape(obj.Description ?? ""));

            table.AddRow(rows.ToArray());
        }

        AnsiConsole.Write(table);
    }

    private static void RenderLevels(IReadOnlyList<LsObject> objects)
    {
        var showDescription = objects.Any(o => !string.IsNullOrEmpty(o.Description));

        var table = NewTable("Name", "Column");
        if (showDescription)
            table.AddColumn(new TableColumn("Description") { Alignment = Justify.Left });

        foreach (var obj in objects)
        {
            var rows = new List<string>
            {
                Styling.MarkupEscape(obj.Name),
                Styling.MarkupEscape(obj.Detail ?? "")
            };
            if (showDescription)
                rows.Add(Styling.MarkupEscape(obj.Description ?? ""));
            table.AddRow(rows.ToArray());
        }

        AnsiConsole.Write(table);
    }

    private static void RenderGeneric(IReadOnlyList<LsObject> objects, bool noMultiline)
    {
        var showDescription = objects.Any(o => !string.IsNullOrEmpty(o.Description));
        var detailLines = objects.ToDictionary(o => o, o => DetailLines(o, noMultiline));

        var table = NewTable("Name", "Detail");
        if (showDescription)
            table.AddColumn(new TableColumn("Description") { Alignment = Justify.Left });

        foreach (var obj in objects)
        {
            var lines = detailLines[obj];
            var rows = new List<string>
            {
                Styling.MarkupEscape(obj.Name),
                Styling.MarkupEscape(string.Join("\n", lines))
            };
            if (showDescription)
                rows.Add(Styling.MarkupEscape(obj.Description ?? ""));
            table.AddRow(rows.ToArray());
        }

        AnsiConsole.Write(table);
    }

    private static Table NewTable(params string[] headers)
        => Styling.NewTable(headers);

    private static string Count(LsObject obj, ModelObjectKind kind)
        => obj.ChildCounts.GetValueOrDefault(kind).ToString();

    private static string BoolText(bool value)
        => Styling.BoolText(value);

    private static string ColumnDataTypeDisplay(LsObject obj)
        => DataTypeDisplay(Projected(obj, "dataType"));

    private static string Projected(LsObject obj, string jsonKey)
        => obj.Projected.GetValueOrDefault(jsonKey) as string ?? "";

    private static string DataTypeDisplay(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "int64" => "Integer / Whole Number (int64)",
            "decimal" => "Currency / Fixed Decimal Number (decimal)",
            "string" => "String / Text",
            "double" => "Decimal Number (double)",
            "boolean" or "bool" => "Boolean / True/False",
            "datetime" => "DateTime / Date/Time",
            _ => value
        };

    private static IReadOnlyList<string> ExpressionLines(LsObject obj, bool noMultiline)
        => DetailLines(obj.Expression ?? obj.Detail ?? "", noMultiline);

    private static IReadOnlyList<string> DetailLines(LsObject obj, bool noMultiline)
        => DetailLines(obj.Expression ?? obj.Detail ?? "", noMultiline);

    private static IReadOnlyList<string> DetailLines(string detail, bool noMultiline)
    {
        if (noMultiline)
            return [Collapse(detail)];

        var lines = detail
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Replace("\t", "    ")
            .Split('\n')
            .Select(l => l.TrimEnd())
            .ToList();

        while (lines.Count > 0 && lines[0].Length == 0)
            lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        if (lines.Count == 0)
            return [""];

        var indent = lines.Where(l => l.Length > 0).Min(l => l.Length - l.TrimStart().Length);
        return indent == 0 ? lines : lines.Select(l => l.Length >= indent ? l[indent..] : l).ToList();
    }

    private static string Collapse(string text)
    {
        var single = Whitespace().Replace(text, " ").Trim();
        return single.Length > MaxCollapsedDetail
            ? single[..(MaxCollapsedDetail - 3)] + "..."
            : single;
    }

    private static int KindOrder(ModelObjectKind kind) => kind switch
    {
        ModelObjectKind.Column => 0,
        ModelObjectKind.Measure => 1,
        ModelObjectKind.Hierarchy => 2,
        ModelObjectKind.Level => 3,
        ModelObjectKind.Partition => 4,
        ModelObjectKind.Relationship => 5,
        ModelObjectKind.Role => 6,
        ModelObjectKind.RoleMember => 7,
        ModelObjectKind.Perspective => 8,
        ModelObjectKind.Culture => 9,
        _ => 10
    };

    private static string KindPlural(ModelObjectKind kind) => kind switch
    {
        ModelObjectKind.Column => "Columns",
        ModelObjectKind.Measure => "Measures",
        ModelObjectKind.Hierarchy => "Hierarchies",
        ModelObjectKind.Level => "Levels",
        ModelObjectKind.Partition => "Partitions",
        ModelObjectKind.Relationship => "Relationships",
        ModelObjectKind.Role => "Roles",
        ModelObjectKind.RoleMember => "Members",
        ModelObjectKind.Perspective => "Perspectives",
        ModelObjectKind.Culture => "Cultures",
        _ => kind.ToString() + "s"
    };

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
