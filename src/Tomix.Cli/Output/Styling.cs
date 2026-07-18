using System.Globalization;
using Spectre.Console;

namespace Tomix.Cli.Output;

internal static class Palette
{
    public static readonly Color Sage = new(0x3E, 0x92, 0x87);
    public static readonly Color Lav = new(0x8E, 0x7B, 0xB8);
    public static readonly Color Terra = new(0xB5, 0x80, 0x5C);
    public static readonly Color Harbor = new(0x4E, 0x8A, 0xB5);
    public static readonly Color Moss = new(0x5C, 0x9D, 0x52);
    public static readonly Color Amber = new(0xB5, 0x83, 0x2F);
    public static readonly Color Rose = new(0xC2, 0x5E, 0x5E);
    public static readonly Color Slate = new(0x76, 0x80, 0x89);
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
