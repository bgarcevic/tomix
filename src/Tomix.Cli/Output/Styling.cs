using System.Globalization;
using System.Text;
using Spectre.Console;
using Tomix.Core.Dax;

namespace Tomix.Cli.Output;

// Hue and chroma are the original design; each color's lightness is tuned so every role keeps
// ≥3.3:1 contrast on both dark and light terminal backgrounds (the practical ceiling — 4.5:1 on
// both is mathematically impossible for one palette), and so same-view roles separate by
// lightness as well as hue, which keeps them distinguishable for red-green color-blind users.
// Construction and thresholds: docs/cli-color-strategy.md; enforced by PaletteTests.
internal static class Palette
{
    public static readonly Color Sage = new(0x34, 0x89, 0x7E);
    public static readonly Color Lav = new(0x85, 0x72, 0xAF);
    public static readonly Color Terra = new(0x96, 0x64, 0x42);
    public static readonly Color Harbor = new(0x45, 0x82, 0xAC);
    public static readonly Color Moss = new(0x40, 0x81, 0x39);
    public static readonly Color Amber = new(0xB0, 0x7E, 0x2A);
    public static readonly Color Rose = new(0xCC, 0x67, 0x66);
    public static readonly Color Orchid = new(0xCF, 0x67, 0xAC);
    public static readonly Color Slate = new(0x75, 0x7F, 0x88);
}

internal static class Styling
{
    public static string Title(string text) => $"[bold {Palette.Sage.ToMarkup()}]{MarkupEscape(text)}[/]";

    public static string Bold(string text) => $"[bold]{MarkupEscape(text)}[/]";

    public static string Success(string text) => $"[{Palette.Moss.ToMarkup()}]{MarkupEscape(text)}[/]";

    public static string Warning(string text) => $"[{Palette.Amber.ToMarkup()}]{MarkupEscape(text)}[/]";

    public static string Error(string text) => $"[bold {Palette.Rose.ToMarkup()}]{MarkupEscape(text)}[/]";

    public static string Muted(string text) => $"[{Palette.Slate.ToMarkup()}]{MarkupEscape(text)}[/]";

    public static string Path(string text) => $"[{Palette.Harbor.ToMarkup()}]{MarkupEscape(text)}[/]";

    public static string Value(string text) => $"[{Palette.Terra.ToMarkup()}]{MarkupEscape(text)}[/]";

    public static string Option(string text) => $"[{Palette.Lav.ToMarkup()}]{MarkupEscape(text)}[/]";

    public static string KeyValue(string label, string value)
        => $"[bold]{MarkupEscape(label)}[/] {MarkupEscape(value)}";

    public static string Guidance(string text) => $"[{Palette.Slate.ToMarkup()}]{MarkupEscape(text)}[/]";

    public static string MarkupEscape(string text)
        => text.Replace("[", "[[").Replace("]", "]]");

    /// <summary>
    /// A DAX expression as Spectre markup, syntax-highlighted from
    /// <see cref="DaxLanguage.Classify"/>: keywords, functions, strings, comments, and
    /// table/column references each take the palette role they read as. Unstyled text (and all
    /// markup) is escaped, so a DAX <c>[Column]</c> can never inject markup of its own. Pass
    /// <paramref name="measureNames"/> (from <c>DaxModelNames.MeasureNames</c>) to resolve
    /// bracketed references to measures, which get their own role. Use only in human output;
    /// JSON/CSV paths stay markup-free.
    /// </summary>
    public static string DaxMarkup(string expression, IReadOnlySet<string>? measureNames = null)
    {
        var spans = DaxLanguage.Classify(expression, measureNames);
        var markup = new StringBuilder(expression.Length);
        var position = 0;

        foreach (var span in spans)
        {
            if (span.Start > position)
                Plain(markup, expression[position..span.Start]);

            var text = expression.Substring(span.Start, span.Length);
            var style = ClassificationStyle(span.Classification);
            if (style is null)
                Plain(markup, text);
            else
                markup.Append('[').Append(style).Append(']').Append(MarkupEscape(text)).Append("[/]");

            position = span.Start + span.Length;
        }

        if (position < expression.Length)
            Plain(markup, expression[position..]);

        return markup.ToString();
    }

    /// <summary>The palette style for a DAX classification, or null when printed plain.</summary>
    private static string? ClassificationStyle(DaxTextClassification classification) =>
        classification switch
        {
            DaxTextClassification.Keyword => Palette.Lav.ToMarkup(),
            DaxTextClassification.Function => Palette.Harbor.ToMarkup(),
            DaxTextClassification.TableName => Palette.Sage.ToMarkup(),
            DaxTextClassification.ColumnReference => Palette.Moss.ToMarkup(),
            DaxTextClassification.MeasureReference => Palette.Orchid.ToMarkup(),
            DaxTextClassification.Variable => Palette.Terra.ToMarkup(),
            DaxTextClassification.StringLiteral or DaxTextClassification.Number
                or DaxTextClassification.QueryParameter => Palette.Amber.ToMarkup(),
            DaxTextClassification.Comment => Palette.Slate.ToMarkup(),
            DaxTextClassification.DefinitionName => "bold",
            _ => null,
        };

    /// <summary>
    /// An expression for human output — the one entry point renderers use for DAX-bearing text.
    /// DAX comes back syntax-highlighted via <see cref="DaxMarkup"/>; anything else (M, plain
    /// text) is markup-escaped. <paramref name="isDax"/> comes from
    /// <c>DaxExpressions.IsDaxValue</c>/<c>IsDaxExpression</c> (Tomix.App.Dax);
    /// <paramref name="measureNames"/> resolves measure references so they color apart from
    /// columns. A trailing preview note ("... (+2 lines)") rides in <paramref name="suffix"/> so
    /// it stays plain even in a highlighted cell. Use only in human output; JSON/CSV paths stay
    /// markup-free.
    /// </summary>
    public static string ExpressionMarkup(
        bool isDax,
        string text,
        IReadOnlySet<string>? measureNames = null,
        string? suffix = null)
        => isDax
            ? DaxMarkup(text, measureNames) + MarkupEscape(suffix ?? "")
            : MarkupEscape(text + suffix);

    private static void Plain(StringBuilder markup, string text) => markup.Append(MarkupEscape(text));

    public static Table NewTable(params string[] headers)
    {
        var table = new Table()
            .RoundedBorder()
            .BorderColor(Palette.Slate);

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
        => value ? $"[{Palette.Slate.ToMarkup()}]True[/]" : "False";

    public static string SeverityMarkup(string severity) => severity switch
    {
        "Error" => $"[bold {Palette.Rose.ToMarkup()}]Error[/]",
        "Warning" => $"[bold {Palette.Amber.ToMarkup()}]Warning[/]",
        "Info" => $"[{Palette.Sage.ToMarkup()}]Info[/]",
        _ => MarkupEscape(severity)
    };

    /// <summary>A severity-colored "● SEVERITY" heading for grouped report sections.</summary>
    public static string SeverityHeading(string severity) => severity switch
    {
        "Error" => $"[bold {Palette.Rose.ToMarkup()}]● ERROR[/]",
        "Warning" => $"[bold {Palette.Amber.ToMarkup()}]● WARNING[/]",
        "Info" => $"[{Palette.Sage.ToMarkup()}]● INFO[/]",
        _ => MarkupEscape(severity)
    };
}
