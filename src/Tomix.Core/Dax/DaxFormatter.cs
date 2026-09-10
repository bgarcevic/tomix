namespace Tomix.Core.Dax;

/// <summary>
/// The outcome of an offline format attempt: whether the formatted code could be produced, the
/// text to use either way (the input when <see cref="Success"/> is false), and, when formatting
/// was declined, the 1-based line of the first token or comment the printed result would have
/// changed — null when no single spot is responsible.
/// </summary>
public sealed record DaxFormatResult(bool Success, string Formatted, int? FirstDifferenceLine);

/// <summary>
/// Offline DAX formatting over a single expression: the code is tokenized, parsed, and printed
/// through a layout engine that keeps a construct on one line when it fits and expands it fully
/// when it does not. Formatting never changes the code — whitespace, line breaks, and the
/// upper-casing of keywords and known function names are the only permitted differences — and
/// when the printed result would alter a token, a string, or a comment, the input is returned
/// unchanged. Works air-gapped: no network, no model, no dependencies beyond the BCL.
/// </summary>
public static class DaxFormatter
{
    public const int DefaultMaximumLineLength = Engine.DaxCodeFormatter.DefaultMaximumLineLength;

    /// <summary>
    /// Formats <paramref name="dax"/>, returning the input unchanged when it cannot be formatted.
    /// The result uses the platform's line endings.
    /// </summary>
    public static string Format(string dax, int maximumLineLength = DefaultMaximumLineLength)
    {
        TryFormat(dax, maximumLineLength, out var formatted);
        return formatted;
    }

    /// <summary>
    /// Formats <paramref name="dax"/> and reports where formatting was declined: the result
    /// carries the text to use (the input itself when <see cref="DaxFormatResult.Success"/> is
    /// false) and the line of the first difference the printer would have introduced.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maximumLineLength"/> is outside the 20–500 character range the printer supports.
    /// </exception>
    public static DaxFormatResult TryFormat(string dax, int maximumLineLength = DefaultMaximumLineLength)
    {
        if (string.IsNullOrWhiteSpace(dax))
            return new DaxFormatResult(false, dax, null);

        try
        {
            if (!Engine.DaxCodeFormatter.TryFormatChecked(dax, maximumLineLength, out var prepared, out var printed))
            {
                // Declined: the text to use is the prepared input, and the first difference is
                // found by comparing it against the rejected printed output.
                return new DaxFormatResult(false, NormalizeLineEndings(prepared), FirstDifferenceLine(prepared, printed));
            }

            return new DaxFormatResult(true, NormalizeLineEndings(printed), null);
        }
        catch (ArgumentException ex) when (ex is not ArgumentOutOfRangeException)
        {
            // The text unwraps to nothing (for example a fenced block with no code inside). An
            // out-of-range line length is a caller bug and must keep throwing.
            return new DaxFormatResult(false, dax, null);
        }
    }

    /// <summary>
    /// Formats <paramref name="dax"/> and reports whether the result is the formatted code.
    /// Returns false and yields the input unchanged when the text contains no DAX at all or when
    /// printing it would not preserve its tokens, strings, and comments.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maximumLineLength"/> is outside the 20–500 character range the printer supports.
    /// </exception>
    public static bool TryFormat(string dax, int maximumLineLength, out string formatted)
    {
        var result = TryFormat(dax, maximumLineLength);
        formatted = result.Formatted;
        return result.Success;
    }

    /// <summary>Formats <paramref name="dax"/> at the default line length. See the two-argument overload.</summary>
    public static bool TryFormat(string dax, out string formatted)
        => TryFormat(dax, DefaultMaximumLineLength, out formatted);

    /// <summary>
    /// Maps a declined format back into the prepared source: the 1-based line of the first token
    /// whose printed form differs, or — when the token streams agree and the check failed on
    /// comments — the line of the first comment whose text would change. Null when the streams
    /// cannot be aligned at all, leaving no single spot to point at.
    /// </summary>
    private static int? FirstDifferenceLine(string prepared, string printed)
    {
        var sourceSignature = Engine.DaxCodeFormatter.Signature(prepared);
        var printedSignature = Engine.DaxCodeFormatter.Signature(printed);

        var index = 0;
        while (index < sourceSignature.Count
               && index < printedSignature.Count
               && string.Equals(sourceSignature[index], printedSignature[index], StringComparison.Ordinal))
            index++;

        if (index < sourceSignature.Count && index < printedSignature.Count)
        {
            // The token streams diverge here, and the signature is 1:1 with the non-empty,
            // non-end-of-file tokens, so this index names the source token to point at.
            var content = Engine.DaxLexer.Tokenize(prepared)
                .Where(token => token.Kind != Engine.DaxTokenKind.EndOfFile && token.Text.Length > 0)
                .ToList();
            if (index < content.Count)
                return LineOf(prepared, content[index].Start);
            return null;
        }

        // Tokens agree, so the check failed on the comments (the engine compares them trimmed
        // and separately from the code). Walk them in the engine's order — leading before
        // trailing, token by token — and report the first comment whose text would change.
        var sourceComments = Comments(Engine.DaxLexer.Tokenize(prepared));
        var printedComments = Comments(Engine.DaxLexer.Tokenize(printed));
        for (var i = 0; i < sourceComments.Count && i < printedComments.Count; i++)
        {
            if (!string.Equals(
                    sourceComments[i].Text.Trim(),
                    printedComments[i].Text.Trim(),
                    StringComparison.Ordinal))
                return LineOf(prepared, sourceComments[i].Start);
        }

        return null;
    }

    private static string NormalizeLineEndings(string text) =>
        string.Join(Environment.NewLine, text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));

    private static List<Engine.DaxComment> Comments(List<Engine.DaxToken> tokens)
    {
        var comments = new List<Engine.DaxComment>();
        foreach (var token in tokens)
        {
            comments.AddRange(token.LeadingComments);
            comments.AddRange(token.TrailingComments);
        }

        return comments;
    }

    private static int LineOf(string text, int offset)
    {
        var line = 1;
        for (var i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n')
                line++;
        }

        return line;
    }
}
