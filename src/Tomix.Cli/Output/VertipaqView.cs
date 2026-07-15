using System.Globalization;
using Tomix.Core.Vertipaq;

namespace Tomix.Cli.Output;

/// <summary>
/// Pure, presentation-only helpers for <c>vertipaq</c> output: section/field resolution,
/// size-descending ordering, <c>--top</c> truncation, and the relative-size bar. Kept free of
/// Spectre/console dependencies so all of it can be unit tested directly (the BpaRunView
/// pattern). JSON/CSV raw values come from the same field specs so every format agrees.
/// </summary>
internal static class VertipaqView
{
    internal const int BarWidth = 10;

    internal enum Section { Tables, Columns, Relationships, Partitions }

    internal enum FieldKind { Text, Integer, Percent, Ratio, Bool, Bar }

    internal sealed record ViewOptions(
        bool Tables,
        bool Columns,
        bool Relationships,
        bool Partitions,
        bool All,
        bool Detail,
        bool Stats,
        IReadOnlyList<string>? Fields,
        int? Top);

    internal sealed record FieldSpec(string Token, string Header, FieldKind Kind);

    /// <summary>One rendered section: resolved fields plus raw row values in field order.</summary>
    internal sealed record SectionTable(
        Section Section,
        string Title,
        IReadOnlyList<FieldSpec> Fields,
        IReadOnlyList<IReadOnlyList<object?>> Rows,
        int TotalCount);

    // ── Section selection ───────────────────────────────────────────────────

    /// <summary>
    /// Data sections in canonical order. No view flag selects the default columns view —
    /// unless <c>--stats</c> alone was passed, which shows the summary only.
    /// </summary>
    internal static IReadOnlyList<Section> ResolveSections(ViewOptions options)
    {
        if (options.All)
            return [Section.Tables, Section.Columns, Section.Relationships, Section.Partitions];

        var sections = new List<Section>();
        if (options.Tables) sections.Add(Section.Tables);
        if (options.Columns) sections.Add(Section.Columns);
        if (options.Relationships) sections.Add(Section.Relationships);
        if (options.Partitions) sections.Add(Section.Partitions);

        if (sections.Count == 0 && !options.Stats)
            sections.Add(Section.Columns);

        return sections;
    }

    // ── Field vocabulary ────────────────────────────────────────────────────

    private static readonly IReadOnlyDictionary<Section, IReadOnlyList<FieldSpec>> AllFields =
        new Dictionary<Section, IReadOnlyList<FieldSpec>>
        {
            [Section.Tables] =
            [
                new("name", "Table", FieldKind.Text),
                new("rows", "Rows", FieldKind.Integer),
                new("size", "Size", FieldKind.Integer),
                new("%db", "% DB", FieldKind.Percent),
                new("cols", "Columns", FieldKind.Integer),
                new("bar", "", FieldKind.Bar),
                new("data", "Data", FieldKind.Integer),
                new("dict", "Dictionary", FieldKind.Integer),
                new("hier", "Hierarchies", FieldKind.Integer),
                new("rels", "Relationships", FieldKind.Integer),
                new("userhier", "User Hier", FieldKind.Integer),
                new("partitions", "Partitions", FieldKind.Integer),
                new("segments", "Segments", FieldKind.Integer),
                new("refd", "Referenced", FieldKind.Bool)
            ],
            [Section.Columns] =
            [
                new("name", "Column", FieldKind.Text),
                new("card", "Cardinality", FieldKind.Integer),
                new("size", "Size", FieldKind.Integer),
                new("%tbl", "% Table", FieldKind.Percent),
                new("%db", "% DB", FieldKind.Percent),
                new("bar", "", FieldKind.Bar),
                new("data", "Data", FieldKind.Integer),
                new("dict", "Dictionary", FieldKind.Integer),
                new("hier", "Hierarchies", FieldKind.Integer),
                new("encoding", "Encoding", FieldKind.Text),
                new("segments", "Segments", FieldKind.Integer),
                new("table", "Table", FieldKind.Text),
                new("type", "Data Type", FieldKind.Text),
                new("sel", "Selectivity", FieldKind.Ratio),
                new("partitions", "Partitions", FieldKind.Integer),
                new("hidden", "Hidden", FieldKind.Bool),
                new("refd", "Referenced", FieldKind.Bool),
                new("state", "State", FieldKind.Text)
            ],
            [Section.Relationships] =
            [
                new("name", "Relationship", FieldKind.Text),
                new("size", "Size", FieldKind.Integer),
                new("fromcard", "From Cardinality", FieldKind.Integer),
                new("tocard", "To Cardinality", FieldKind.Integer),
                new("bar", "", FieldKind.Bar),
                new("missing", "Missing Keys", FieldKind.Integer),
                new("invalid", "Invalid Rows", FieldKind.Integer),
                new("ratio", "1:M Ratio", FieldKind.Ratio),
                new("active", "Active", FieldKind.Bool),
                new("xfilter", "Cross Filter", FieldKind.Text)
            ],
            [Section.Partitions] =
            [
                new("table", "Table", FieldKind.Text),
                new("name", "Partition", FieldKind.Text),
                new("rows", "Rows", FieldKind.Integer),
                new("size", "Size", FieldKind.Integer),
                new("segments", "Segments", FieldKind.Integer),
                new("bar", "", FieldKind.Bar),
                new("state", "State", FieldKind.Text),
                new("type", "Type", FieldKind.Text),
                new("mode", "Mode", FieldKind.Text),
                new("refreshed", "Refreshed", FieldKind.Text)
            ]
        };

    private static readonly IReadOnlyDictionary<Section, string[]> DefaultTokens =
        new Dictionary<Section, string[]>
        {
            [Section.Tables] = ["name", "rows", "size", "%db", "cols", "bar"],
            [Section.Columns] = ["name", "card", "size", "%tbl", "%db", "bar"],
            [Section.Relationships] = ["name", "size", "fromcard", "tocard", "bar"],
            [Section.Partitions] = ["table", "name", "rows", "size", "segments", "bar"]
        };

    private static readonly IReadOnlyDictionary<Section, string[]> DetailTokens =
        new Dictionary<Section, string[]>
        {
            [Section.Tables] = ["data", "dict", "hier", "rels", "userhier", "partitions", "segments"],
            [Section.Columns] = ["data", "dict", "hier", "encoding", "segments"],
            [Section.Relationships] = ["missing", "invalid", "ratio", "active", "xfilter"],
            [Section.Partitions] = ["state", "type", "mode", "refreshed"]
        };

    internal static IReadOnlyList<string> ValidTokens(Section section)
        => AllFields[section].Select(f => f.Token).ToList();

    /// <summary>
    /// Resolves the field list for a section: an explicit <c>--fields</c> list wins, otherwise
    /// the defaults (plus the breakdown set with <c>--detail</c>). Tokens are case-insensitive.
    /// </summary>
    internal static IReadOnlyList<FieldSpec> ResolveFields(
        Section section, bool detail, IReadOnlyList<string>? fields)
    {
        var specs = AllFields[section];

        if (fields is { Count: > 0 })
            return fields
                .Select(token => specs.First(s => s.Token.Equals(token.Trim(), StringComparison.OrdinalIgnoreCase)))
                .ToList();

        var tokens = detail
            ? DefaultTokens[section].Concat(DetailTokens[section])
            : DefaultTokens[section];

        // --detail replaces the bar with the size breakdown; keep it last if still present.
        var resolved = tokens.Select(t => specs.First(s => s.Token == t)).ToList();
        if (detail && resolved.RemoveAll(s => s.Kind == FieldKind.Bar) > 0)
            resolved.Add(specs.First(s => s.Kind == FieldKind.Bar));

        return resolved;
    }

    /// <summary>Returns the unknown tokens in <paramref name="fields"/> for a section (empty = valid).</summary>
    internal static IReadOnlyList<string> UnknownTokens(Section section, IReadOnlyList<string> fields)
    {
        var valid = ValidTokens(section);
        return fields
            .Select(f => f.Trim())
            .Where(f => !valid.Contains(f, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Splits a raw <c>--fields</c> value on commas, dropping empty entries.</summary>
    internal static IReadOnlyList<string> ParseFieldList(string? fields)
        => string.IsNullOrWhiteSpace(fields)
            ? []
            : fields.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    // ── Section building ────────────────────────────────────────────────────

    internal static IReadOnlyList<SectionTable> BuildSections(VertipaqModelStats stats, ViewOptions options)
        => ResolveSections(options)
            .Select(section => BuildSection(stats, section, options))
            .ToList();

    private static SectionTable BuildSection(VertipaqModelStats stats, Section section, ViewOptions options)
    {
        var fields = ResolveFields(section, options.Detail, options.Fields);

        return section switch
        {
            Section.Tables => Build(
                section, "Tables by size", fields, options.Top,
                stats.Tables, t => t.TableSize, TableCell),
            Section.Columns => Build(
                section, "Columns by size", fields, options.Top,
                stats.Columns, c => c.TotalSize, ColumnCell),
            Section.Relationships => Build(
                section, "Relationships by size", fields, options.Top,
                stats.Relationships, r => r.UsedSize, RelationshipCell),
            _ => Build(
                section, "Partitions by size", fields, options.Top,
                stats.Partitions, p => p.DataSize, PartitionCell)
        };
    }

    private static SectionTable Build<T>(
        Section section,
        string title,
        IReadOnlyList<FieldSpec> fields,
        int? top,
        IReadOnlyList<T> items,
        Func<T, long> sizeOf,
        Func<T, string, object?> cell)
    {
        var ordered = items.OrderByDescending(sizeOf).ToList();
        var shown = top is { } limit ? ordered.Take(limit).ToList() : ordered;
        var max = ordered.Count == 0 ? 0 : ordered.Max(sizeOf);

        var rows = shown
            .Select(item => (IReadOnlyList<object?>)fields
                .Select(f => f.Kind == FieldKind.Bar ? Bar(sizeOf(item), max) : cell(item, f.Token))
                .ToList())
            .ToList();

        return new SectionTable(section, title, fields, rows, ordered.Count);
    }

    private static object? TableCell(VertipaqTableStats t, string token) => token switch
    {
        "name" => t.TableName,
        "rows" => t.RowCount,
        "size" => t.TableSize,
        "%db" => t.PercentageDatabase,
        "cols" => t.ColumnCount,
        "data" => t.ColumnsDataSize,
        "dict" => t.ColumnsDictionarySize,
        "hier" => t.ColumnsHierarchiesSize,
        "rels" => t.RelationshipsSize,
        "userhier" => t.UserHierarchiesSize,
        "partitions" => t.PartitionCount,
        "segments" => t.SegmentCount,
        "refd" => t.IsReferenced,
        _ => null
    };

    private static object? ColumnCell(VertipaqColumnStats c, string token) => token switch
    {
        "name" => $"{c.TableName}[{c.ColumnName}]",
        "card" => c.Cardinality,
        "size" => c.TotalSize,
        "%tbl" => c.PercentageTable,
        "%db" => c.PercentageDatabase,
        "data" => c.DataSize,
        "dict" => c.DictionarySize,
        "hier" => c.HierarchiesSize,
        "encoding" => c.Encoding,
        "segments" => c.SegmentCount,
        "table" => c.TableName,
        "type" => c.DataType,
        "sel" => c.Selectivity,
        "partitions" => c.PartitionCount,
        "hidden" => c.IsHidden,
        "refd" => c.IsReferenced,
        "state" => c.State,
        _ => null
    };

    private static object? RelationshipCell(VertipaqRelationshipStats r, string token) => token switch
    {
        "name" => r.RelationshipName,
        "size" => r.UsedSize,
        "fromcard" => r.FromCardinality,
        "tocard" => r.ToCardinality,
        "missing" => r.MissingKeys,
        "invalid" => r.InvalidRows,
        "ratio" => r.OneToManyRatio,
        "active" => r.IsActive,
        "xfilter" => r.CrossFilteringBehavior,
        _ => null
    };

    private static object? PartitionCell(VertipaqPartitionStats p, string token) => token switch
    {
        "table" => p.TableName,
        "name" => p.PartitionName,
        "rows" => p.RowCount,
        "size" => p.DataSize,
        "segments" => p.SegmentCount,
        "state" => p.State,
        "type" => p.Type,
        "mode" => p.Mode,
        "refreshed" => p.RefreshedTime?.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "",
        _ => null
    };

    // ── Summary ─────────────────────────────────────────────────────────────

    internal static IReadOnlyList<(string Label, string Value)> BuildSummary(VertipaqModelStats stats)
    {
        var summary = new List<(string, string)>
        {
            ("Model", stats.ModelName),
            ("Total size", FormatBytes(stats.TotalSize)),
            ("Tables", stats.TableCount.ToString(CultureInfo.InvariantCulture)),
            ("Columns", stats.ColumnCount.ToString(CultureInfo.InvariantCulture)),
            ("Max rows", Styling.Number(stats.MaxRowCount))
        };

        if (stats.Tables.Count > 0)
        {
            var largestTable = stats.Tables.MaxBy(t => t.TableSize)!;
            summary.Add(("Largest table",
                $"{largestTable.TableName} ({FormatBytes(largestTable.TableSize)})"));
        }

        if (stats.Columns.Count > 0)
        {
            var largestColumn = stats.Columns.MaxBy(c => c.TotalSize)!;
            summary.Add(("Largest column",
                $"{largestColumn.TableName}[{largestColumn.ColumnName}] ({FormatBytes(largestColumn.TotalSize)})"));
        }

        if (stats.ExtractionDate is { } date)
            summary.Add(("Extracted",
                date.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)));

        return summary;
    }

    // ── Formatting primitives ───────────────────────────────────────────────

    /// <summary>Relative-size bar: <c>█████░░░░░</c>. Zero/invalid max renders empty.</summary>
    internal static string Bar(long value, long max, int width = BarWidth)
    {
        if (max <= 0 || value <= 0 || width <= 0)
            return new string('░', Math.Max(width, 0));

        var filled = (int)Math.Round(width * Math.Min(value, max) / (double)max, MidpointRounding.AwayFromZero);
        if (filled == 0 && value > 0)
            filled = 1; // a non-zero value always shows at least one tick

        return new string('█', filled) + new string('░', width - filled);
    }

    /// <summary>Invariant one-decimal percentage, e.g. <c>12.3 %</c>. Values are 0-100.</summary>
    internal static string Percent(double value)
        => double.IsFinite(value) ? value.ToString("0.0", CultureInfo.InvariantCulture) + " %" : "0.0 %";

    /// <summary>Invariant two-decimal ratio, e.g. selectivity or 1:M ratio.</summary>
    internal static string RatioText(double? value)
        => value is { } v && double.IsFinite(v) ? v.ToString("0.00", CultureInfo.InvariantCulture) : "";

    /// <summary>Grouped byte count, e.g. <c>1,300 B</c>. Human output only.</summary>
    internal static string FormatBytes(long value) => Styling.Number(value) + " B";
}
