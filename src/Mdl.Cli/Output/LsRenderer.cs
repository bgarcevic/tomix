using System.Text;
using System.Text.RegularExpressions;
using Mdl.App.Ls;
using Mdl.Core.Models;

namespace Mdl.Cli.Output;

/// <summary>
/// Renders an <see cref="LsModelResult"/> for human (text) output. A homogeneous list of tables
/// gets dedicated count columns; anything else falls back to a uniform path/kind/detail listing.
/// </summary>
internal sealed partial class LsRenderer
{
    private const char Esc = (char)27;
    private const int MaxCollapsedDetail = 80;

    public static void Render(LsModelResult data, bool pathsOnly, bool noMultiline)
    {
        if (pathsOnly)
        {
            foreach (var obj in data.Objects)
                Console.WriteLine(obj.Path);

            return;
        }

        var cyan = $"{Esc}[36m";
        var dim = $"{Esc}[2m";
        var reset = $"{Esc}[0m";

        var allTables = data.Objects.Count > 0 && data.Objects.All(o => o.Kind == ModelObjectKind.Table);

        Console.WriteLine($"{cyan}{data.ModelName}{reset}");
        Console.WriteLine();
        var noun = allTables ? "table" : "object";
        Console.WriteLine($"{data.Objects.Count} {noun}{(data.Objects.Count == 1 ? "" : "s")}");

        if (data.Objects.Count == 0)
            return;

        Console.WriteLine();

        if (allTables)
            RenderTables(data.Objects, dim, reset);
        else
            RenderUniform(data.Objects, noMultiline, dim, reset);
    }

    private static void RenderTables(IReadOnlyList<LsObject> objects, string dim, string reset)
    {
        var showDescription = objects.Any(o => !string.IsNullOrEmpty(o.Description));

        var nameWidth = Math.Max("NAME".Length, objects.Max(o => o.Name.Length));
        var colWidth = NumberColumnWidth("COLUMNS", objects, ModelObjectKind.Column);
        var measWidth = NumberColumnWidth("MEASURES", objects, ModelObjectKind.Measure);
        var partWidth = NumberColumnWidth("PARTITIONS", objects, ModelObjectKind.Partition);
        const int hiddenWidth = 6; // "HIDDEN"

        var header = new StringBuilder()
            .Append("NAME".PadRight(nameWidth)).Append("  ")
            .Append("COLUMNS".PadLeft(colWidth)).Append("  ")
            .Append("MEASURES".PadLeft(measWidth)).Append("  ")
            .Append("PARTITIONS".PadLeft(partWidth)).Append("  ")
            .Append("HIDDEN".PadRight(hiddenWidth));
        if (showDescription)
            header.Append("  ").Append("DESCRIPTION");
        Console.WriteLine(header.ToString().TrimEnd());

        foreach (var obj in objects)
        {
            var row = new StringBuilder()
                .Append(obj.Name.PadRight(nameWidth)).Append("  ")
                .Append(Count(obj, ModelObjectKind.Column).PadLeft(colWidth)).Append("  ")
                .Append(Count(obj, ModelObjectKind.Measure).PadLeft(measWidth)).Append("  ")
                .Append(Count(obj, ModelObjectKind.Partition).PadLeft(partWidth)).Append("  ")
                .Append((obj.Hidden ? "yes" : "no").PadRight(hiddenWidth));
            if (showDescription)
                row.Append("  ").Append(obj.Description ?? "");

            var line = row.ToString().TrimEnd();
            Console.WriteLine(obj.Hidden ? $"{dim}{line}{reset}" : line);
        }
    }

    // Uniform listing for non-table / mixed results: PATH, KIND, DETAIL, and a trailing DESCRIPTION
    // column only when at least one object has a description.
    private static void RenderUniform(IReadOnlyList<LsObject> objects, bool noMultiline, string dim, string reset)
    {
        var showDescription = objects.Any(o => !string.IsNullOrEmpty(o.Description));

        var pathWidth = Math.Max("PATH".Length, objects.Max(o => o.Path.Length));
        var kindWidth = Math.Max("KIND".Length, objects.Max(o => KindLabel(o.Kind).Length));
        var continuation = new string(' ', pathWidth + 2 + kindWidth + 2);
        var detailWidth = showDescription
            ? Math.Max("DETAIL".Length, objects.Max(o => DetailLines(o, noMultiline)[0].Length))
            : 0;

        var header = $"{"PATH".PadRight(pathWidth)}  {"KIND".PadRight(kindWidth)}  ";
        header += showDescription ? $"{"DETAIL".PadRight(detailWidth)}  DESCRIPTION" : "DETAIL";
        Console.WriteLine(header);

        foreach (var obj in objects)
        {
            var detailLines = DetailLines(obj, noMultiline);
            var firstDetail = showDescription ? detailLines[0].PadRight(detailWidth) : detailLines[0];
            var head = $"{obj.Path.PadRight(pathWidth)}  {KindLabel(obj.Kind).PadRight(kindWidth)}  {firstDetail}";
            if (showDescription)
                head += $"  {obj.Description ?? ""}";
            head = head.TrimEnd();
            Console.WriteLine(obj.Hidden ? $"{dim}{head}{reset}" : head);

            for (var i = 1; i < detailLines.Count; i++)
                Console.WriteLine($"{continuation}{detailLines[i]}");
        }
    }

    private static string Count(LsObject obj, ModelObjectKind kind)
        => obj.ChildCounts.GetValueOrDefault(kind).ToString();

    private static int NumberColumnWidth(string header, IReadOnlyList<LsObject> objects, ModelObjectKind kind)
        => Math.Max(header.Length, objects.Max(o => Count(o, kind).Length));

    private static IReadOnlyList<string> DetailLines(LsObject obj, bool noMultiline)
    {
        var detail = obj.Expression ?? obj.Detail ?? "";

        if (noMultiline)
            return [Collapse(detail)];

        var lines = detail
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Replace("\t", "    ")
            .Split('\n')
            .Select(l => l.TrimEnd())
            .ToList();

        // Drop leading/trailing blank lines (TMDL stores many expressions starting on a new line).
        while (lines.Count > 0 && lines[0].Length == 0)
            lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        if (lines.Count == 0)
            return [""];

        // Dedent by the smallest common indentation so expressions don't drift far right.
        var indent = lines.Where(l => l.Length > 0).Min(l => l.Length - l.TrimStart().Length);
        return indent == 0 ? lines : lines.Select(l => l.Length >= indent ? l[indent..] : l).ToList();
    }

    private static string Collapse(string text)
    {
        var single = Whitespace().Replace(text, " ").Trim();
        return single.Length > MaxCollapsedDetail
            ? single[..(MaxCollapsedDetail - 1)] + "…"
            : single;
    }

    private static string KindLabel(ModelObjectKind kind) => kind switch
    {
        ModelObjectKind.RoleMember => "member",
        _ => kind.ToString().ToLowerInvariant()
    };

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
