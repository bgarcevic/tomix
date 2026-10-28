using System.Text.RegularExpressions;

namespace Tomix.App.Test;

/// <param name="Name">Test name: the .dax path relative to the discovery root, '/'-normalized,
/// without the extension (e.g. <c>totals/sales-by-region</c>).</param>
public sealed record DiscoveredTest(string Name, string DaxPath, string ExpectedPath);

/// <summary>A <c>.expected.json</c> snapshot with no sibling <c>.dax</c> test, left behind by a deleted or renamed test.</summary>
/// <param name="Name">The test name the snapshot belonged to, in the same form as <see cref="DiscoveredTest.Name"/>.</param>
/// <param name="File">Full path of the snapshot file.</param>
public sealed record OrphanedSnapshot(string Name, string File);

/// <summary>
/// Finds test files for <c>tx test</c>: a single <c>.dax</c> file, or a directory searched
/// recursively for <c>*.dax</c> (ordinal-sorted by name for deterministic run order). Each
/// test pairs with a sibling <c>&lt;name&gt;.expected.json</c> snapshot.
/// </summary>
public static class TestDiscovery
{
    private const string SnapshotSuffix = ".expected.json";

    // Matches how the filesystem pairs files: Sales.dax owns sales.expected.json only where
    // names are case-insensitive.
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

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

    /// <summary>
    /// Snapshots under a test directory that no <c>.dax</c> file pairs with. A single-file
    /// <paramref name="path"/> has no orphans.
    /// </summary>
    public static IReadOnlyList<OrphanedSnapshot> FindOrphans(string path)
    {
        if (!Directory.Exists(path))
            return [];

        var root = Path.GetFullPath(path);
        return Orphans(
            root,
            Directory.EnumerateFiles(root, "*" + SnapshotSuffix, SearchOption.AllDirectories),
            Directory.EnumerateFiles(root, "*.dax", SearchOption.AllDirectories));
    }

    /// <summary>The snapshot paths that no test path pairs with, ordinal-sorted by name.</summary>
    public static IReadOnlyList<OrphanedSnapshot> Orphans(
        string root, IEnumerable<string> snapshotPaths, IEnumerable<string> daxPaths)
    {
        var paired = new HashSet<string>(daxPaths.Select(ExpectedPathFor), PathComparer);
        return snapshotPaths
            .Where(snapshot => !paired.Contains(snapshot))
            .Select(snapshot => new OrphanedSnapshot(
                Path.GetRelativePath(root, snapshot)[..^SnapshotSuffix.Length].Replace('\\', '/'),
                snapshot))
            .OrderBy(orphan => orphan.Name, StringComparer.Ordinal)
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

    /// <summary>
    /// "found " for an unfiltered selection, otherwise what narrowed it, for example
    /// "match --filter totals/* and --tag smoke ", to complete "No .dax test files ... under".
    /// </summary>
    public static string Selection(string? filter, IReadOnlyList<string>? tags)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(filter))
            parts.Add($"--filter {filter}");
        if (tags is { Count: > 0 })
            parts.Add($"--tag {string.Join(", ", tags)}");
        return parts.Count == 0 ? "found " : $"match {string.Join(" and ", parts)} ";
    }

    private static string ExpectedPathFor(string daxPath)
        => Path.ChangeExtension(daxPath, SnapshotSuffix);
}
