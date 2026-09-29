using System.Globalization;
using Spectre.Console;
using Tomix.App.Summary;

namespace Tomix.Cli.Output;

/// <summary>
/// Text view for <c>summary</c>, laid out like the <c>get</c> view: a title with the kind, aligned
/// muted keys (the JSON keys), then a <c>Contents</c> section of object counts. Zero counts render
/// muted, the way <c>get --all</c> renders defaults.
/// </summary>
internal static class SummaryRenderer
{
    private const string Indent = "  ";

    public static void Render(SummaryModelResult result)
    {
        // A terminal wraps at its width; redirected output stays unwrapped so a long source path
        // is never split across lines for grep or a file (as in 'get').
        if (!Console.IsOutputRedirected)
        {
            RenderCore(result);
            return;
        }

        var width = AnsiConsole.Profile.Width;
        AnsiConsole.Profile.Width = int.MaxValue;
        try
        {
            RenderCore(result);
        }
        finally
        {
            AnsiConsole.Profile.Width = width;
        }
    }

    /// <summary>The stderr footer: where to go next. Commentary, so it never reaches a redirected stdout.</summary>
    public static void RenderHint()
        => StdErr.MarkupLine(Styling.Guidance(
            $"{Indent}→ List objects with 'tx ls --type <type>'; 'tx get .' shows the model's properties."));

    private static void RenderCore(SummaryModelResult result)
    {
        AnsiConsole.MarkupLine($"{Styling.Title(result.Name)}  {Styling.Muted("Model")}");

        var settings = new List<(string Key, string Value)> { ("source", result.Source) };
        if (result.Database is not null)
            settings.Add(("database", result.Database));
        if (result.Format is not null)
            settings.Add(("format", result.Format));
        settings.Add(("compatibilityLevel", result.CompatibilityLevel.ToString(CultureInfo.InvariantCulture)));
        if (result.Culture is not null)
            settings.Add(("culture", result.Culture));
        if (result.DefaultMode is not null)
            settings.Add(("defaultMode", result.DefaultMode));

        Console.WriteLine();
        var keyWidth = settings.Max(row => row.Key.Length);
        foreach (var (key, value) in settings)
            WriteRow(Indent, keyWidth, key, Styling.MarkupEscape(value));

        var counts = result.Counts;
        (string Key, int Value)[] contents =
        [
            ("tables", counts.Tables),
            ("columns", counts.Columns),
            ("measures", counts.Measures),
            ("relationships", counts.Relationships),
            ("roles", counts.Roles),
            ("partitions", counts.Partitions),
            ("calculationGroups", counts.CalculationGroups),
            ("perspectives", counts.Perspectives),
            ("cultures", counts.Cultures),
        ];

        Console.WriteLine();
        AnsiConsole.MarkupLine($"{Indent}{Styling.Bold("Contents")}");
        var countKeyWidth = contents.Max(row => row.Key.Length);
        var countWidth = contents.Max(row => Count(row.Value).Length);
        foreach (var (key, value) in contents)
        {
            var text = Count(value).PadLeft(countWidth);
            WriteRow(Indent + Indent, countKeyWidth, key, value == 0 ? Styling.Muted(text) : text);
        }
    }

    // A one-row grid per line, as in 'get': a long value wraps inside its own column instead of
    // back to the margin, and every row shares the section's key width.
    private static void WriteRow(string indent, int keyWidth, string key, string valueMarkup)
    {
        var grid = new Grid()
            .AddColumn(new GridColumn { Width = keyWidth, NoWrap = true, Padding = new Padding(indent.Length, 0, 2, 0) })
            .AddColumn(new GridColumn { Padding = new Padding(0) });
        grid.AddRow(new Markup(Styling.Muted(key)), new Markup(valueMarkup));
        AnsiConsole.Write(grid);
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
