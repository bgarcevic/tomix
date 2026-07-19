using System.Text.RegularExpressions;

namespace Tomix.App.Test;

/// <param name="Name">Test name: the .dax path relative to the discovery root, '/'-normalized,
/// without the extension (e.g. <c>totals/sales-by-region</c>).</param>
public sealed record DiscoveredTest(string Name, string DaxPath, string ExpectedPath);

/// <summary>
/// Finds test files for <c>tx test</c>: a single <c>.dax</c> file, or a directory searched
/// recursively for <c>*.dax</c> (ordinal-sorted by name for deterministic run order). Each
/// test pairs with a sibling <c>&lt;name&gt;.expected.json</c> snapshot.
/// </summary>
public static class TestDiscovery
{
    public static IReadOnlyList<DiscoveredTest> Discover(string path)
    {
        if (File.Exists(path))
        {
            var fullPath = Path.GetFullPath(path);
            return [new DiscoveredTest(
                Path.GetFileNameWithoutExtension(fullPath),
                fullPath,
                ExpectedPathFor(fullPath))];
        }

        var root = Path.GetFullPath(path);
        return Directory.EnumerateFiles(root, "*.dax", SearchOption.AllDirectories)
            .Select(daxPath => new DiscoveredTest(
                Path.ChangeExtension(Path.GetRelativePath(root, daxPath), null).Replace('\\', '/'),
                daxPath,
                ExpectedPathFor(daxPath)))
            .OrderBy(test => test.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Case-insensitive <c>*</c>/<c>?</c> wildcard match on the test name; a blank pattern matches everything.</summary>
    public static bool MatchesFilter(string name, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return true;

        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase);
    }

    private static string ExpectedPathFor(string daxPath)
        => Path.ChangeExtension(daxPath, ".expected.json");
}
