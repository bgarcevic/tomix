using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
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
/// (camelCase, one row per line, "\n" line endings, BOM-less UTF-8, trailing newline) so an
/// unchanged re-record is a no-op write and snapshot diffs are minimal. Reading accepts any JSON
/// layout, so snapshots written in the older one-cell-per-line layout still load.
/// </summary>
public static partial class TestSnapshotFile
{
    public const int CurrentVersion = 1;

    public static TestSnapshot FromResult(ModelQueryResult result, string queryHash)
        => new(
            CurrentVersion,
            queryHash,
            result.Columns,
            result.Rows.Select(row => (IReadOnlyList<string?>)row.Select(TestValueFormatter.Format).ToList()).ToList());

    /// <summary>
    /// SHA-256 (lowercase hex) of the query text with line endings and outer whitespace
    /// normalized and <c>tx:</c> directive lines removed, so changing a test's settings is not
    /// reported as a query change.
    /// </summary>
    public static string ComputeQueryHash(string query)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(TestCompareOptions.WithoutDirectives(query).Trim())));

    /// <summary>
    /// Indented JSON with one column and one row per line, so a changed cell diffs as its whole
    /// row (<c>-["East", "1200.00"]</c> / <c>+["East", "1234.50"]</c>) instead of a bare value.
    /// Strings use relaxed escaping so non-ASCII names stay readable in the file.
    /// </summary>
    public static string Serialize(TestSnapshot snapshot)
    {
        var json = new StringBuilder();
        json.Append("{\n");
        json.Append("  \"version\": ").Append(snapshot.Version.ToString(CultureInfo.InvariantCulture)).Append(",\n");
        json.Append("  \"querySha256\": ").Append(Quote(snapshot.QuerySha256)).Append(",\n");
        AppendLines(json, "columns", snapshot.Columns.Select(c => $"{{ \"name\": {Quote(c.Name)}, \"type\": {Quote(c.Type)} }}"));
        json.Append(",\n");
        AppendLines(json, "rows", snapshot.Rows.Select(row => "[" + string.Join(", ", row.Select(Quote)) + "]"));
        json.Append("\n}\n");
        return json.ToString();
    }

    private static void AppendLines(StringBuilder json, string name, IEnumerable<string> items)
    {
        var lines = items.ToList();
        json.Append("  \"").Append(name).Append("\": ");
        if (lines.Count == 0)
        {
            json.Append("[]");
            return;
        }

        json.Append("[\n    ").Append(string.Join(",\n    ", lines)).Append("\n  ]");
    }

    private static string Quote(string? value)
        => value is null ? "null" : $"\"{JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping)}\"";

    /// <summary>
    /// Loads a snapshot; null with a non-null <paramref name="error"/> when the file is
    /// malformed, incomplete, or has an unsupported version. Existence is the caller's
    /// concern (a missing file is the Missing outcome, not an error).
    /// </summary>
    public static TestSnapshot? Load(string path, out string? error)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize(File.ReadAllText(path), SnapshotJsonContext.Default.TestSnapshot);
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

    // Reading only; Serialize writes the file layout itself.
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(TestSnapshot))]
    private sealed partial class SnapshotJsonContext : JsonSerializerContext;
}
