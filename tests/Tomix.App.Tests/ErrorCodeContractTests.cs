using System.Text.RegularExpressions;

namespace Tomix.App.Tests;

/// <summary>
/// Drift guard for the error codes emitted by <c>tx</c>. Codes are public API surface
/// (see <c>docs/error-codes.md</c>), so every <c>TOMIX_*</c> literal in <c>src/</c> must be
/// documented, and codes retired by the mutation-code migration must not come back.
/// The JSON envelope shape itself is pinned against the production serializer in
/// <c>Tomix.Cli.Tests.ErrorOutputContractTests</c>.
/// </summary>
public sealed class ErrorCodeContractTests
{
    private static readonly Regex TomixLiteral = new("\"(TOMIX_[A-Z0-9_]+)\"", RegexOptions.Compiled);

    /// <summary>
    /// Every <c>TOMIX_*</c> literal in production source — diagnostic codes and the supported
    /// environment variables alike — is part of the documented surface. A new code that never
    /// reaches <c>docs/error-codes.md</c> fails here.
    /// </summary>
    [Fact]
    public void EveryTomixLiteralInSource_IsDocumented()
    {
        var documentation = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "error-codes.md"));

        var undocumented = SourceLiterals()
            .Where(code => !documentation.Contains(code, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            undocumented.Count == 0,
            $"Undocumented TOMIX_* literals in src/ — add them to docs/error-codes.md: {string.Join(", ", undocumented)}");
    }

    /// <summary>
    /// The codes retired by the "Unified Mutation Error Codes" migration must not be emitted
    /// again; the migration table in the docs is the source of truth for what is retired.
    /// </summary>
    [Fact]
    public void RetiredMutationErrorCodes_AreNotEmittedBySource()
    {
        var retired = RetiredCodesFromDocs();
        Assert.NotEmpty(retired); // guards against the docs table being renamed away

        var resurrected = SourceLiterals().Intersect(retired, StringComparer.Ordinal).ToList();

        Assert.True(
            resurrected.Count == 0,
            $"Retired error codes are emitted again by src/ — use the unified TOMIX_MUTATION_* codes: {string.Join(", ", resurrected)}");
    }

    /// <summary>Distinct <c>TOMIX_*</c> string literals across all production sources.</summary>
    private static SortedSet<string> SourceLiterals()
    {
        var literals = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match match in TomixLiteral.Matches(File.ReadAllText(file)))
                literals.Add(match.Groups[1].Value);
        }

        return literals;
    }

    /// <summary>
    /// Old codes from the first column of the migration table in <c>docs/error-codes.md</c>.
    /// Parsing stops at the next heading so the "What did not change" list — codes that are
    /// still valid — is not treated as retired.
    /// </summary>
    private static SortedSet<string> RetiredCodesFromDocs()
    {
        var retired = new SortedSet<string>(StringComparer.Ordinal);
        var inMigrationTable = false;

        foreach (var line in File.ReadLines(Path.Combine(RepositoryRoot(), "docs", "error-codes.md")))
        {
            if (line.StartsWith("## Migration: Unified Mutation Error Codes", StringComparison.Ordinal))
            {
                inMigrationTable = true;
                continue;
            }

            if (!inMigrationTable)
                continue;

            if (line.StartsWith('#'))
                break;

            var match = Regex.Match(line, @"^\|\s*`(TOMIX_[A-Z0-9_]+)`\s*\|");
            if (match.Success)
                retired.Add(match.Groups[1].Value);
        }

        return retired;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tomix.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
    }
}
