using System.Text;
using Spectre.Console;
using Tomix.App.Session;

namespace Tomix.Cli.Output;

/// <summary>
/// What <c>tx interactive</c> shows when it starts at a terminal: the TOMIX logo, the open model
/// (or how to open one), the keys to know, and a tip. Commentary, so it goes to stderr.
/// </summary>
internal static class InteractiveBanner
{
    private static readonly string[] Logo =
    [
        "████████╗██╗  ██╗",
        "╚══██╔══╝╚██╗██╔╝",
        "   ██║    ╚███╔╝ ",
        "   ██║    ██╔██╗ ",
        "   ██║   ██╔╝ ██╗",
        "   ╚═╝   ╚═╝  ╚═╝"
    ];

    private static readonly string[] Tips =
    [
        "begin … commit groups several edits into one undo step.",
        "history lists every change; undo and redo walk through them.",
        "Tab completes commands and options; Up and Down recall earlier lines.",
        "get Sales/Revenue shows an object's properties; find searches expressions.",
        "Any command takes --output-format json for a JSON result.",
        "save -o <folder> writes a copy; the session keeps saving to its source."
    ];

    /// <summary>Text beside the logo starts this many columns after it.</summary>
    private const int Gap = 4;

    public static void Write(IAnsiConsole console, string version, SessionModelInfo? model, Random? random = null)
    {
        var width = console.Profile.Width;
        console.WriteLine();
        WriteLogo(console, version, width);
        console.WriteLine();

        if (model is null)
        {
            console.MarkupLine($"  {Styling.Muted("No model open.")} {Styling.Bold("connect <path>")} {Styling.Muted("opens a TMDL folder or .bim file;")} {Styling.Bold("connect --recent")} {Styling.Muted("picks a recent one.")}");
        }
        else
        {
            WriteModel(console, model);
        }

        console.WriteLine();
        console.MarkupLine($"  {Styling.Bold("help")} {Styling.Muted("lists commands;")} {Styling.Bold("<command> --help")} {Styling.Muted("shows details.")}");
        console.MarkupLine($"  {Styling.Muted("Edits stay in memory until you run")} {Styling.Bold("save")}{Styling.Muted(";")} {Styling.Bold("undo")} {Styling.Muted("and")} {Styling.Bold("redo")} {Styling.Muted("step through them.")}");
        console.MarkupLine($"  {Styling.Bold("exit")}{Styling.Muted(",")} {Styling.Bold("quit")} {Styling.Muted("or")} {Styling.Bold("Ctrl+D")} {Styling.Muted("on an empty line leaves; it asks before discarding unsaved changes.")}");
        console.WriteLine();
        var tip = Tips[(random ?? Random.Shared).Next(Tips.Length)];
        console.MarkupLine($"  {Styling.Muted("Tip:")} {Styling.Muted(tip)}");
        console.Write(new Rule().RuleStyle(Palette.Muted));
    }

    /// <summary>The model block the welcome shows.</summary>
    public static void WriteModel(IAnsiConsole console, SessionModelInfo model)
    {
        var summary = model.Summary;
        var kind = Directory.Exists(model.Source) ? "TMDL folder" : Path.GetExtension(model.Source).TrimStart('.') + " file";
        console.MarkupLine($"  {Styling.Title("Model:")} {Styling.Bold(summary.Name)}  {Styling.Muted($"{kind} · compatibility level {summary.CompatibilityLevel}")}");
        console.MarkupLine("  " + Styling.Muted(string.Join(
            "  ",
            Count(summary.Tables, "table"),
            Count(summary.Columns, "column"),
            Count(summary.Measures, "measure"),
            Count(summary.Relationships, "relationship"),
            Count(summary.Roles, "role"))));
        console.MarkupLine($"  {Styling.Muted("Saves to:")} {Styling.Path(model.Source)}");
    }

    /// <summary>
    /// The logo, bold in the terminal's own foreground with its outline dimmed, and the title beside it.
    /// A narrow terminal gets the title alone.
    /// </summary>
    private static void WriteLogo(IAnsiConsole console, string version, int width)
    {
        var logoWidth = Logo.Max(line => line.Length);
        if (width < logoWidth + 4)
        {
            console.MarkupLine($"  {Styling.Title("tomix")} {Styling.Muted($"interactive · v{version}")}");
            return;
        }

        string?[] side = [null, Styling.Bold("T O M I X"), Styling.Muted(new string('─', 25)), Styling.Muted("Interactive model session"), Styling.Muted($"v{version}"), null];
        var beside = width >= logoWidth + 2 + Gap + 25;
        for (var row = 0; row < Logo.Length; row++)
        {
            var line = "  " + Shade(Logo[row]);
            if (beside && side[row] is { } text)
                line += new string(' ', Gap) + text;
            console.MarkupLine(line);
        }

        if (!beside)
            console.MarkupLine($"  {Styling.Muted($"Interactive model session · v{version}")}");
    }

    private static string Shade(string line)
    {
        var markup = new StringBuilder();
        foreach (var character in line)
        {
            if (character == ' ')
            {
                markup.Append(' ');
                continue;
            }

            markup.Append(character == '█' ? "[bold]" : "[dim]").Append(character).Append("[/]");
        }

        return markup.ToString();
    }

    private static string Count(int count, string noun)
        => $"{count} {noun}{(count == 1 ? "" : "s")}";
}
