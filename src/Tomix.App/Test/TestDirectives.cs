namespace Tomix.App.Test;

/// <summary>Everything a test's <c>tx:</c> directive lines set (see <see cref="TestCompareOptions.ReadDirectives"/>).</summary>
/// <param name="Compare">How the result compares with its snapshot or baseline.</param>
/// <param name="Assertions">What the result must satisfy.</param>
/// <param name="Tags">The test's tags (<c>tags=smoke,nightly</c>), distinct, in the order written; empty when it has none.</param>
public sealed record TestDirectives(TestCompareOptions Compare, TestAssertions Assertions, IReadOnlyList<string> Tags);

/// <summary>Tag names and <c>--tag</c> selection for <c>tx test</c> and <c>tx test list</c>.</summary>
public static class TestTags
{
    /// <summary>A tag is one or more letters, digits, <c>-</c> or <c>_</c>.</summary>
    public static bool IsValid(string tag)
        => tag.Length > 0 && tag.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>True when no tag is wanted, or the test has any of the wanted ones (case-insensitive).</summary>
    public static bool Matches(IReadOnlyList<string> tags, IReadOnlyList<string>? wanted)
        => wanted is not { Count: > 0 } || tags.Any(tag => wanted.Contains(tag, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// The tests that carry any of the <paramref name="wanted"/> tags; all of them when none is
    /// wanted. A test whose file cannot be read or whose directives are invalid is kept, so the
    /// run reports its problem instead of silently skipping it.
    /// </summary>
    public static IReadOnlyList<DiscoveredTest> Select(IReadOnlyList<DiscoveredTest> tests, IReadOnlyList<string>? wanted)
    {
        if (wanted is not { Count: > 0 })
            return tests;

        return tests.Where(test =>
        {
            string query;
            try
            {
                query = File.ReadAllText(test.DaxPath);
            }
            catch (IOException)
            {
                return true;
            }

            var directives = TestCompareOptions.Exact.ReadDirectives(query, out _);
            return directives is null || Matches(directives.Tags, wanted);
        }).ToList();
    }
}
