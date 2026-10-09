using System.Globalization;
using System.Text;
using Spectre.Console;
using Tomix.Core.Dax;
using Tomix.Core.M;

namespace Tomix.Cli.Output;

/// <summary>Which highlighter <see cref="Styling.ExpressionMarkup"/> applies.</summary>
internal enum ExpressionLanguage
{
    Plain,
    Dax,
    M,
}

// ANSI-16 roles, not RGB: each color is a terminal palette index, so the user's own theme decides
// the shade and its contrast. Only red, green, yellow, cyan, magenta and bright blue are used:
// normal blue is unreadable on the Windows Terminal default (Campbell), and the other bright codes
// turn grey in Solarized. Color never carries meaning alone; every status also has a glyph or a
// word. Everything else is bold, dim, or plain. Rationale: docs/cli-color-strategy.md; the role
// mapping is pinned by PaletteTests.
internal static class Palette
{
    public static readonly Color Error = Color.Maroon;
    public static readonly Color Success = Color.Green;
    public static readonly Color Warning = Color.Olive;
    public static readonly Color Info = Color.Teal;
    public static readonly Color Keyword = Color.Purple;
    public static readonly Color Function = Color.Blue;
    public static readonly Color Reference = Color.Teal;
    public static readonly Color Literal = Color.Green;

    /// <summary>Secondary text, borders and rules: the terminal's own foreground, dimmed.</summary>
    public static readonly Style Muted = new(decoration: Decoration.Dim);
}

internal static class Styling
{
    public static string Title(string text) => Bold(text);

    public static string Bold(string text) => $"[bold]{MarkupEscape(text)}[/]";

    public static string Success(string text) => $"[{Palette.Success.ToMarkup()}]{MarkupEscape(text)}[/]";

    public static string Warning(string text) => $"[{Palette.Warning.ToMarkup()}]{MarkupEscape(text)}[/]";

    public static string Error(string text) => $"[bold {Palette.Error.ToMarkup()}]{MarkupEscape(text)}[/]";

    public static string Muted(string text) => $"[dim]{MarkupEscape(text)}[/]";

    public static string Path(string text) => MarkupEscape(text);

    public static string Value(string text) => MarkupEscape(text);

    public static string Option(string text) => Bold(text);

    /// <summary>
    /// A stand-in the user replaces, such as <c>&lt;path&gt;</c> or <c>[options]</c> in help: dim, so
    /// the command or flag it follows stays the thing the eye lands on.
    /// </summary>
    public static string Placeholder(string text) => Muted(text);

    /// <summary>
    /// One label and value, both plain. For two or more rows use <see cref="KeyValueLines"/>, which
    /// lines the values up.
    /// </summary>
    public static string KeyValue(string label, string value)
        => $"{MarkupEscape(label)} {MarkupEscape(value)}";

    /// <summary>
    /// A block of label/value rows as markup lines, both plain, with every value starting in one
    /// column two spaces past the longest label: alignment, not color, carries the structure.
    /// </summary>
    public static IEnumerable<string> KeyValueLines(IReadOnlyCollection<(string Label, string Value)> rows, string indent = "")
    {
        var width = rows.Count == 0 ? 0 : rows.Max(row => row.Label.Length);
        return rows.Select(row => indent + MarkupEscape(row.Label.PadRight(width)) + "  " + MarkupEscape(row.Value));
    }

    /// <summary>Writes <see cref="KeyValueLines"/> to stdout.</summary>
    public static void WriteKeyValues(IReadOnlyCollection<(string Label, string Value)> rows, string indent = "")
    {
        foreach (var line in KeyValueLines(rows, indent))
            AnsiConsole.MarkupLine(line);
    }

    public static string Guidance(string text) => Muted(text);

    public static string MarkupEscape(string text)
        => text.Replace("[", "[[").Replace("]", "]]");

    /// <summary>
    /// A DAX expression as Spectre markup, syntax-highlighted from
    /// <see cref="DaxLanguage.Classify"/>: keywords, functions, measure references, literals, and
    /// comments each take their palette role; tables, columns and variables stay plain. Unstyled text (and all
    /// markup) is escaped, so a DAX <c>[Column]</c> can never inject markup of its own. Pass
    /// <paramref name="measureNames"/> (from <c>DaxModelNames.MeasureNames</c>) to resolve
    /// bracketed references to measures, which get their own role. Use only in human output;
    /// JSON/CSV paths stay markup-free.
    /// </summary>
    public static string DaxMarkup(string expression, IReadOnlySet<string>? measureNames = null)
        => SpansMarkup(
            expression,
            DaxLanguage.Classify(expression, measureNames)
                .Select(span => (span.Start, span.Length, ClassificationStyle(span.Classification))));

    /// <summary>
    /// A Power Query (M) expression as Spectre markup, syntax-highlighted from
    /// <see cref="MLanguage.Classify"/> with the same palette roles as DAX: keywords, library
    /// functions, literals, and comments; steps and field access stay plain. Unstyled text
    /// is escaped, so a field access <c>[Amount]</c> can never inject markup. Use only in human
    /// output; JSON/CSV paths stay markup-free.
    /// </summary>
    public static string MMarkup(string expression)
        => SpansMarkup(
            expression,
            MLanguage.Classify(expression)
                .Select(span => (span.Start, span.Length, ClassificationStyle(span.Classification))));

    private static string SpansMarkup(string expression, IEnumerable<(int Start, int Length, string? Style)> spans)
    {
        var markup = new StringBuilder(expression.Length);
        var position = 0;

        foreach (var (start, length, style) in spans)
        {
            if (start > position)
                Plain(markup, expression[position..start]);

            var text = expression.Substring(start, length);
            if (style is null)
                Plain(markup, text);
            else
                markup.Append('[').Append(style).Append(']').Append(MarkupEscape(text)).Append("[/]");

            position = start + length;
        }

        if (position < expression.Length)
            Plain(markup, expression[position..]);

        return markup.ToString();
    }

    /// <summary>The palette style for a DAX classification, or null when printed plain.</summary>
    private static string? ClassificationStyle(DaxTextClassification classification) =>
        classification switch
        {
            DaxTextClassification.Keyword => Palette.Keyword.ToMarkup(),
            DaxTextClassification.Function => Palette.Function.ToMarkup(),
            DaxTextClassification.MeasureReference => Palette.Reference.ToMarkup(),
            DaxTextClassification.StringLiteral or DaxTextClassification.Number
                or DaxTextClassification.QueryParameter => Palette.Literal.ToMarkup(),
            DaxTextClassification.Comment => "dim",
            DaxTextClassification.DefinitionName => "bold",
            _ => null,
        };

    /// <summary>
    /// The palette style for an M classification, or null when printed plain. M reuses the DAX
    /// roles: step and field definitions read as DAX variables and field access as columns, so
    /// all three stay plain.
    /// </summary>
    private static string? ClassificationStyle(MTextClassification classification) =>
        classification switch
        {
            MTextClassification.Keyword => Palette.Keyword.ToMarkup(),
            MTextClassification.Function => Palette.Function.ToMarkup(),
            MTextClassification.StringLiteral or MTextClassification.Number
                or MTextClassification.Literal => Palette.Literal.ToMarkup(),
            MTextClassification.Comment => "dim",
            _ => null,
        };

    /// <summary>
    /// An expression for human output — the one entry point renderers use for expression text.
    /// DAX comes back syntax-highlighted via <see cref="DaxMarkup"/>, M via
    /// <see cref="MMarkup"/>, and plain text is markup-escaped. <paramref name="language"/> comes
    /// from <c>DaxExpressions.IsDaxValue</c>/<c>IsDaxExpression</c> (Tomix.App.Dax) and
    /// <c>MExpressions.IsMValue</c>/<c>IsMExpression</c> (Tomix.App.M);
    /// <paramref name="measureNames"/> resolves DAX measure references so they color apart from
    /// columns. A trailing preview note ("... (+2 lines)") rides in <paramref name="suffix"/> so
    /// it stays plain even in a highlighted cell. Use only in human output; JSON/CSV paths stay
    /// markup-free.
    /// </summary>
    public static string ExpressionMarkup(
        ExpressionLanguage language,
        string text,
        IReadOnlySet<string>? measureNames = null,
        string? suffix = null)
        => language switch
        {
            ExpressionLanguage.Dax => DaxMarkup(text, measureNames) + MarkupEscape(suffix ?? ""),
            ExpressionLanguage.M => MMarkup(text) + MarkupEscape(suffix ?? ""),
            _ => MarkupEscape(text + suffix),
        };

    private static void Plain(StringBuilder markup, string text) => markup.Append(MarkupEscape(text));

    /// <summary>
    /// Rounded borders in a terminal; ASCII borders when stdout is redirected
    /// (<see cref="StdOut.PlainWhenRedirected"/> turns Unicode off there).
    /// </summary>
    public static TableBorder Border
        => AnsiConsole.Profile.Capabilities.Unicode ? TableBorder.Rounded : TableBorder.Ascii;

    /// <summary>Line guides in a terminal; ASCII guides when stdout is redirected (see <see cref="Border"/>).</summary>
    public static TreeGuide TreeGuide
        => AnsiConsole.Profile.Capabilities.Unicode ? TreeGuide.Line : TreeGuide.Ascii;

    /// <summary>Unicode symbols in a terminal; ASCII ones when stdout is redirected (see <see cref="Border"/>).</summary>
    public static Glyphs Glyphs
        => AnsiConsole.Profile.Capabilities.Unicode ? Glyphs.Unicode : Glyphs.Ascii;

    public static Table NewTable(params string[] headers)
    {
        var table = new Table()
            .Border(Border)
            .BorderStyle(Palette.Muted);

        foreach (var header in headers)
            table.AddColumn(new TableColumn(MarkupEscape(header)) { Alignment = Justify.Left });

        return table;
    }

    /// <summary>
    /// Group-formatted integer with invariant culture (e.g. <c>2,014,768</c>).
    /// Use only in human output; JSON/CSV must emit raw numbers via <see cref="System.IFormattable"/>.
    /// </summary>
    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Group-formatted floating point with invariant culture.</summary>
    public static string Number(double value)
        => double.IsFinite(value) ? value.ToString("N0", CultureInfo.InvariantCulture) : "0";

    /// <summary>
    /// One-decimal seconds duration in invariant culture (e.g. <c>6.3s</c>).
    /// Use only in human output.
    /// </summary>
    public static string DurationSeconds(double seconds)
        => seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";

    public static string BoolText(bool value)
        => value ? "[dim]True[/]" : "False";

    public static string SeverityMarkup(string severity) => severity switch
    {
        "Error" => $"[bold {Palette.Error.ToMarkup()}]Error[/]",
        "Warning" => $"[bold {Palette.Warning.ToMarkup()}]Warning[/]",
        "Info" => $"[{Palette.Info.ToMarkup()}]Info[/]",
        _ => MarkupEscape(severity)
    };

    /// <summary>A severity-colored "● SEVERITY" heading for grouped report sections.</summary>
    public static string SeverityHeading(string severity) => severity switch
    {
        "Error" => $"[bold {Palette.Error.ToMarkup()}]{Glyphs.Bullet} ERROR[/]",
        "Warning" => $"[bold {Palette.Warning.ToMarkup()}]{Glyphs.Bullet} WARNING[/]",
        "Info" => $"[{Palette.Info.ToMarkup()}]{Glyphs.Bullet} INFO[/]",
        _ => MarkupEscape(severity)
    };
}
