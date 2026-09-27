using Spectre.Console;
using Tomix.Core.Diagnostics;

namespace Tomix.Cli.Output;

/// <summary>
/// Points at a syntax error in source the user typed: the offending line, then a caret under the
/// error (one per character of the offending token when its end is on the same line). Written to
/// stderr after the error message, for inline expressions only: a model sweep would bury the
/// summary under source excerpts.
/// </summary>
internal static class SyntaxErrorCaret
{
    public static void Write(string source, ExpressionSyntaxError error)
    {
        if (Render(source, error) is not { } lines)
            return;

        var console = StdErr.Console();
        console.MarkupLine(Styling.Muted($"{lines.Gutter} | ") + Styling.MarkupEscape(lines.Source));
        console.MarkupLine(Styling.Muted($"{new string(' ', lines.Gutter.Length)} | ") + Styling.Error(lines.Caret));
    }

    /// <summary>The gutter (line number), source line, and caret line; null when the error has no usable position.</summary>
    internal static (string Gutter, string Source, string Caret)? Render(string source, ExpressionSyntaxError error)
    {
        if (error is not { Line: { } line, Column: { } column } || line < 1 || column < 1)
            return null;

        var sourceLines = source.ReplaceLineEndings("\n").Split('\n');
        if (line > sourceLines.Length)
            return null;

        var text = sourceLines[line - 1];
        var start = Math.Min(column - 1, text.Length);
        var width = error is { EndLine: { } endLine, EndColumn: { } endColumn } && endLine == line && endColumn >= column
            ? endColumn - column + 1
            : 1;

        // Keep tabs in the padding so the caret lines up however the terminal renders them.
        var padding = string.Concat(text[..start].Select(c => c == '\t' ? '\t' : ' '));
        return (line.ToString(System.Globalization.CultureInfo.InvariantCulture), text, padding + new string('^', width));
    }
}
