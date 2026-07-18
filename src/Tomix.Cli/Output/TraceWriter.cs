namespace Tomix.Cli.Output;

/// <summary>
/// Shared <c>--trace</c> destination plumbing for commands that dump raw XMLA trace
/// events (<c>refresh</c>, <c>query</c>): resolves the option value to a destination
/// and opens the matching writer (stderr or file).
/// </summary>
internal static class TraceWriter
{
    /// <summary>
    /// Normalizes the <c>--trace</c> option value. Bare <c>--trace</c> (no value) and
    /// <c>--trace -</c> both map to stderr (<c>"-"</c>); any other non-empty value is treated
    /// as a file path. Returns null only when <c>--trace</c> is absent.
    /// </summary>
    internal static string? ResolvePath(string? traceValue)
        => string.IsNullOrEmpty(traceValue) ? "-" : traceValue;

    /// <summary>
    /// Opens a trace writer for <c>--trace</c>: null (off), "-" or empty (stderr), or a path (file).
    /// Returns null when <paramref name="tracePath"/> is null. Tracing is independent of progress.
    /// </summary>
    internal static TextWriter? Open(string? tracePath, bool quiet)
    {
        if (string.IsNullOrEmpty(tracePath))
            return null;

        if (tracePath == "-")
            return quiet ? TextWriter.Null : NonDisposingTextWriter.Wrap(Console.Error);

        try
        {
            var full = Path.GetFullPath(tracePath);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            return new StreamWriter(full, append: false, System.Text.Encoding.UTF8) { AutoFlush = true };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not open --trace file '{tracePath}': {ex.Message}");
            return NonDisposingTextWriter.Wrap(Console.Error);
        }
    }
}
