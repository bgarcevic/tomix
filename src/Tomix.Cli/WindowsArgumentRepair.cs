namespace Tomix.Cli;

/// <summary>
/// Undoes the argv damage Windows PowerShell 5.1 does to a quoted directory path with a trailing
/// backslash. <c>tx connect '.\My Model.SemanticModel\'</c> reaches the process as
/// <c>".\My Model.SemanticModel\"</c>, and the Windows argv rules read <c>\"</c> as a literal
/// quote, so the program sees <c>.\My Model.SemanticModel"</c> — with any following arguments
/// glued on after a space, because the closing quote was consumed.
/// </summary>
internal static class WindowsArgumentRepair
{
    /// <summary>
    /// Repairs each argument holding exactly one <c>"</c> whose text before the quote is an
    /// existing directory: the quote becomes the trailing separator it replaced, and any text
    /// glued on after it is split back into separate arguments on whitespace. Anything else —
    /// including DAX or M text that legitimately contains quotes — passes through untouched.
    /// </summary>
    internal static string[] Repair(string[] args, Func<string, bool> directoryExists)
    {
        List<string>? repaired = null;
        for (var i = 0; i < args.Length; i++)
        {
            var fixedArgs = TryRepair(args[i], directoryExists);
            if (fixedArgs is null)
            {
                repaired?.Add(args[i]);
                continue;
            }

            repaired ??= [.. args[..i]];
            repaired.AddRange(fixedArgs);
        }

        return repaired is null ? args : [.. repaired];
    }

    private static string[]? TryRepair(string arg, Func<string, bool> directoryExists)
    {
        var quote = arg.IndexOf('"');
        if (quote <= 0 || arg.IndexOf('"', quote + 1) >= 0)
            return null;

        var path = arg[..quote];
        var rest = arg[(quote + 1)..];
        if ((rest.Length > 0 && !char.IsWhiteSpace(rest[0])) || !directoryExists(path))
            return null;

        return [path + '\\', .. rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)];
    }
}
