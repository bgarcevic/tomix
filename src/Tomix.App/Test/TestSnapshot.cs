using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tomix.Core.Models;

namespace Tomix.App.Test;

/// <summary>
/// The expected-result file (<c>&lt;name&gt;.expected.json</c>) stored next to each test's
/// <c>.dax</c> file. Cells are the canonical invariant strings produced by
/// <see cref="TestValueFormatter"/> (JSON null = DAX BLANK) so comparison is exact string
/// equality and git diffs stay readable. <see cref="QuerySha256"/> is a hash of the normalized
/// query text; it never drives pass/fail and only powers the "query changed since the snapshot
/// was recorded" hint.
/// </summary>
public sealed record TestSnapshot(
    int Version,
    string QuerySha256,
    IReadOnlyList<QueryColumn> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows);

/// <summary>
/// Reads and writes <see cref="TestSnapshot"/> files. Serialization is byte-deterministic
/// (indented camelCase, "\n" line endings, BOM-less UTF-8, trailing newline) so an unchanged
/// re-record is a no-op write and snapshot diffs are minimal.
/// </summary>
public static class TestSnapshotFile
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static TestSnapshot FromResult(ModelQueryResult result, string queryHash)
        => new(
            CurrentVersion,
            queryHash,
            result.Columns,
            result.Rows.Select(row => (IReadOnlyList<string?>)row.Select(TestValueFormatter.Format).ToList()).ToList());

    /// <summary>SHA-256 (lowercase hex) of the query text with line endings and outer whitespace normalized.</summary>
    public static string ComputeQueryHash(string query)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(query.ReplaceLineEndings("\n").Trim())));

    public static string Serialize(TestSnapshot snapshot)
        => JsonSerializer.Serialize(snapshot, Options) + "\n";

    /// <summary>
    /// Loads a snapshot; null with a non-null <paramref name="error"/> when the file is
    /// malformed, incomplete, or has an unsupported version. Existence is the caller's
    /// concern (a missing file is the Missing outcome, not an error).
    /// </summary>
    public static TestSnapshot? Load(string path, out string? error)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<TestSnapshot>(File.ReadAllText(path), Options);
            if (snapshot is null || snapshot.Columns is null || snapshot.Rows is null)
            {
                error = "Snapshot file is empty or incomplete.";
                return null;
            }

            if (snapshot.Version != CurrentVersion)
            {
                error = $"Snapshot version {snapshot.Version} is not supported (expected {CurrentVersion}).";
                return null;
            }

            // Guard the comparer's Rows[r][c] indexing: every row must be a real array
            // with exactly one cell per declared column, and every column entry complete.
            if (snapshot.Columns.Any(c => c?.Name is null || c.Type is null))
            {
                error = "Snapshot file is invalid: every column needs a name and a type.";
                return null;
            }

            for (var r = 0; r < snapshot.Rows.Count; r++)
            {
                if (snapshot.Rows[r] is null || snapshot.Rows[r].Count != snapshot.Columns.Count)
                {
                    error = $"Snapshot file is invalid: row {r + 1} has {snapshot.Rows[r]?.Count.ToString() ?? "no"} cell(s), expected {snapshot.Columns.Count}.";
                    return null;
                }
            }

            error = null;
            return snapshot;
        }
        catch (JsonException ex)
        {
            error = $"Snapshot file is not valid JSON: {ex.Message}";
            return null;
        }
        catch (IOException ex)
        {
            error = $"Snapshot file could not be read: {ex.Message}";
            return null;
        }
    }

    /// <summary>Writes the snapshot; returns false (skipping the write) when the file already has identical content.</summary>
    public static bool Save(string path, TestSnapshot snapshot)
    {
        var content = Serialize(snapshot);
        if (File.Exists(path) && File.ReadAllText(path) == content)
            return false;

        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return true;
    }
}
