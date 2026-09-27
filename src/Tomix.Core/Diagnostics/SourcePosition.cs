namespace Tomix.Core.Diagnostics;

/// <summary>Maps an offset in an expression's text to the 1-based line and column people read.</summary>
public static class SourcePosition
{
    /// <summary>
    /// The 1-based line and column of <paramref name="offset"/>. A line break ends at <c>\n</c>, so
    /// CRLF counts once; a tab is one column. An offset at the end of the text is valid: it is where
    /// a missing token would go.
    /// </summary>
    public static (int Line, int Column) Of(string text, int offset)
    {
        var line = 1;
        var lineStart = 0;
        var end = Math.Min(offset, text.Length);
        for (var i = 0; i < end; i++)
        {
            if (text[i] != '\n')
                continue;
            line++;
            lineStart = i + 1;
        }

        return (line, offset - lineStart + 1);
    }
}
