using System.Text.RegularExpressions;

namespace Tomix.App.Mutations;

/// <summary>
/// Recognises the DAX object form (<c>'Table'[Child]</c> or <c>Table[Child]</c>, doubled
/// <c>''</c> = literal apostrophe) that the mutation resolver accepts, so handlers can reason
/// about parents and leaf names in the same shape the resolver does.
/// </summary>
internal static partial class DaxObjectForm
{
    public static bool TryParse(string path, out string table, out string child)
    {
        var match = DaxObjectPath().Match(path.Trim());
        if (!match.Success)
        {
            table = "";
            child = "";
            return false;
        }

        table = match.Groups["qtable"].Success
            ? match.Groups["qtable"].Value.Replace("''", "'", StringComparison.Ordinal)
            : match.Groups["table"].Value;
        table = table.Trim();
        child = match.Groups["object"].Value.Trim();
        return true;
    }

    /// <summary>Converts <c>'Table'[Child]</c> to <c>Table/Child</c>; other paths pass through.</summary>
    public static string Normalize(string path)
        => TryParse(path, out var table, out var child) ? $"{table}/{child}" : path;

    [GeneratedRegex("^(?:'(?<qtable>(?:[^']|'')+)'|(?<table>[^'\\[\\]]+))\\[(?<object>[^\\]]+)\\]$")]
    private static partial Regex DaxObjectPath();
}
