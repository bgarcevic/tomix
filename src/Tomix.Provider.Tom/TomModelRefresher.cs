using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SystemTextJson = System.Text.Json.JsonSerializer;
using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using TabularDatabase = Microsoft.AnalysisServices.Tabular.Database;
using TabularServer = Microsoft.AnalysisServices.Tabular.Server;

namespace Tomix.Provider.Tom;

/// <summary>
/// Builds refresh TMSL and executes it against a connected <see cref="TabularServer"/>.
/// Mirrors <see cref="TomModelDeployer"/>: provider-side helper invoked by
/// <c>TomServerModelSession.RefreshAsync</c> which already owns the live server/database.
/// </summary>
public static class TomModelRefresher
{
    /// <summary>
    /// JSON options that emit <c>\"</c> for embedded quotes instead of <c>\u0022</c>.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string J(string value) => SystemTextJson.Serialize(value, JsonOptions);

    /// <summary>
    /// Refresh type aliases mapped to TMSL canonical values.
    /// TMSL accepts: full, clearValues, calculate, dataOnly, automatic, defragment, add.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> RefreshTypeAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["full"] = "full",
        ["dataonly"] = "dataOnly",
        ["dataOnly"] = "dataOnly",
        ["automatic"] = "automatic",
        ["auto"] = "automatic",
        ["calculate"] = "calculate",
        ["clearvalues"] = "clearValues",
        ["clearValues"] = "clearValues",
        ["defragment"] = "defragment",
        ["add"] = "add",
    };

    public static string NormalizeRefreshType(string? refreshType)
    {
        var value = string.IsNullOrWhiteSpace(refreshType) ? "automatic" : refreshType;
        if (RefreshTypeAliases.TryGetValue(value, out var normalized))
            return normalized;
        throw new InvalidOperationException(
            $"Unknown refresh type '{value}'. Valid: full, dataonly, automatic, calculate, clearvalues, defragment, add.");
    }

    /// <summary>
    /// Builds a TMSL <c>{"refresh":{...}}</c> command without executing it.
    /// Used by <c>--dry-run</c>. The database is implicit in the connection's <c>Initial Catalog</c>.
    /// </summary>
    public static string GenerateRefreshScript(TabularDatabase database, ModelRefreshRequest request)
    {
        var type = NormalizeRefreshType(request.RefreshType);

        // Power BI Service requires "database" in each object entry (the Initial Catalog
        // in the connection string is not enough). Use the explicit Database on the request
        // when set, else fall back to the connected database name.
        var dbName = string.IsNullOrWhiteSpace(request.Database)
            ? (string.IsNullOrWhiteSpace(database.Name) ? database.ID : database.Name)
            : request.Database;

        var sb = new StringBuilder();
        sb.Append("{\"refresh\":{");
        sb.Append("\"type\":\"").Append(type).Append('"');

        // objects: partition scope takes precedence; then table scope; then full model.
        // Power BI Service requires "database" in each object entry (the Initial Catalog
        // in the connection string is not enough).
        if (request.Partitions is { Count: > 0 })
        {
            sb.Append(",\"objects\":[");
            for (var i = 0; i < request.Partitions.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"database\":").Append(J(dbName));
                sb.Append(",\"table\":").Append(J(request.Partitions[i].Table));
                sb.Append(",\"partition\":").Append(J(request.Partitions[i].Partition)).Append('}');
            }
            sb.Append(']');
        }
        else if (request.Tables is { Count: > 0 })
        {
            sb.Append(",\"objects\":[");
            for (var i = 0; i < request.Tables.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"database\":").Append(J(dbName));
                sb.Append(",\"table\":").Append(J(request.Tables[i])).Append('}');
            }
            sb.Append(']');
        }
        else
        {
            // Full model refresh: a single {"database":"<name>"} object tells the server to
            // refresh the entire model. Simpler and faster than enumerating every table.
            sb.Append(",\"objects\":[{\"database\":").Append(J(dbName)).Append("}]");
        }

        // applyRefreshPolicy defaults to true on the server. Only emit when explicitly disabled.
        if (!request.ApplyRefreshPolicy)
            sb.Append(",\"applyRefreshPolicy\":false");

        if (request.EffectiveDate is { } ed)
            sb.Append(",\"effectiveDate\":\"").Append(ed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('"');

        if (request.MaxParallelism is { } mp and > 0)
            sb.Append(",\"maxParallelism\":").Append(mp.ToString(CultureInfo.InvariantCulture));

        sb.Append("}}");
        return sb.ToString();
    }

    /// <summary>
    /// Executes a refresh on the connected server. When <paramref name="progress"/> and/or
    /// <paramref name="traceWriter"/> are non-null, attaches an XMLA SessionTrace to capture
    /// ProgressReport events and feed live per-table row counts and final Query/Read/Total splits.
    /// </summary>
    public static async Task<ModelRefreshResult> RefreshAsync(
        TabularServer server,
        TabularDatabase database,
        ModelRefreshRequest request,
        IProgress<RefreshProgress>? progress,
        TextWriter? traceWriter,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();

        var type = NormalizeRefreshType(request.RefreshType);
        var dbName = string.IsNullOrWhiteSpace(request.Database)
            ? (string.IsNullOrWhiteSpace(database.Name) ? database.ID : database.Name)
            : request.Database;

        var tmsl = GenerateRefreshScript(database, request with { Database = dbName });

        // The known table list lets the trace sink map child-object events (hierarchies,
        // relationships, columns) back to their parent table by matching names in TextData.
        var knownTables = (request.Tables ?? database.Model.Tables.Select(t => t.Name)).ToList();
        // Always attach the trace sink: even when the live display is suppressed (piped stdout,
        // --no-progress, JSON/CSV output) we still want per-table durations in the final summary.
        using var traceSink = RefreshTraceSink.Attach(server, knownTables, progress, traceWriter)
            ?? RefreshTraceSink.AttachSummaryOnly(server, knownTables);

        // server.Execute is synchronous and blocks while ProgressReport events fire on the
        // same thread, which is what feeds the live display. Wrap in Task.Run only if needed.
        var results = await Task.Run(() => server.Execute(tmsl), cancellationToken).ConfigureAwait(false);

        sw.Stop();

        // AMO's Execute returns a result collection that may contain errors/warnings even
        // when no exception is thrown. Surface them so refreshes don't silently no-op.
        var serverErrors = new List<string>();
        if (results is { Count: > 0 })
        {
            foreach (dynamic r in results)
            {
                foreach (dynamic m in r.Messages)
                {
                    var typeName = ((Type)m.GetType()).Name;
                    if (typeName == "XmlaError")
                        serverErrors.Add($"{m.Description}");
                    else if (typeName == "XmlaWarning" && traceWriter is not null)
                        traceWriter.WriteLine($"warning: {m.Description}");
                }
            }
        }

        if (serverErrors.Count > 0)
        {
            // The session trace's ProgressReportError events carry per-table context that the
            // raw XmlaError messages lack. When we have them, format as "<table>: <error>" so the
            // user can see which table failed. Unresolved errors (key "") are appended verbatim.
            var message = BuildErrorMessage(serverErrors, traceSink?.BuildTableErrors());
            throw new InvalidOperationException(message);
        }

        // Merge trace-captured results with the model/request fallback: when the session trace
        // couldn't be attached or no ProgressReport events fired (small model, skipped refresh,
        // server-side restriction), still list the in-scope tables so the summary is useful.
        var tables = MergeTableResults(
            traceSink?.BuildTableResults(),
            BuildTableResultsFromRequest(database, request));

        var totals = Aggregate(tables);
        return new ModelRefreshResult(server.Name, dbName, type, sw.ElapsedMilliseconds, tables, totals);
    }

    private static List<RefreshTableResult> MergeTableResults(
        IReadOnlyList<RefreshTableResult>? fromTrace,
        IReadOnlyList<RefreshTableResult> fallback)
    {
        if (fromTrace is null || fromTrace.Count == 0)
            return fallback.ToList();

        // Replace any fallback entry with the trace version when we have one; keep others.
        var byName = fallback.ToDictionary(t => t.Table, StringComparer.Ordinal);
        foreach (var t in fromTrace)
            byName[t.Table] = t;
        return byName.Values
            .OrderBy(t => t.TotalMs > 0 ? t.TotalMs : long.MaxValue)
            .ThenBy(t => t.Table, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Phase 2 fallback: list the tables that were in scope (or all tables for a full refresh)
    /// with zero rows and durations. Phase 5's trace sink replaces this with real numbers.
    /// </summary>
    private static IReadOnlyList<RefreshTableResult> BuildTableResultsFromRequest(
        TabularDatabase database,
        ModelRefreshRequest request)
    {
        IEnumerable<string> names;
        if (request.Partitions is { Count: > 0 })
            names = request.Partitions.Select(p => p.Table).Distinct(StringComparer.Ordinal);
        else if (request.Tables is { Count: > 0 })
            names = request.Tables;
        else
            names = database.Model.Tables.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal);

        return names
            .Select(n => new RefreshTableResult(n, 0, 0, 0, 0))
            .ToList();
    }

    private static RefreshTableResult? Aggregate(IReadOnlyList<RefreshTableResult> tables)
    {
        if (tables.Count == 0) return null;
        return new RefreshTableResult(
            Table: "Total",
            Rows: tables.Sum(t => t.Rows),
            QueryMs: tables.Sum(t => t.QueryMs),
            ReadMs: tables.Sum(t => t.ReadMs),
            TotalMs: tables.Sum(t => t.TotalMs));
    }

    /// <summary>
    /// Builds the failure message by combining the server's XmlaError descriptions with per-table
    /// context captured by the trace sink. When trace errors are available, the message lists each
    /// failing table with its error(s); otherwise it falls back to the raw server text.
    /// </summary>
    private static string BuildErrorMessage(
        IReadOnlyList<string> serverErrors,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? tableErrors)
    {
        // No trace-captured per-table errors: emit the server text as-is.
        if (tableErrors is null || tableErrors.Count == 0)
            return "Refresh returned errors: " + string.Join("; ", serverErrors);

        var sb = new StringBuilder("Refresh returned errors:");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("Failing tables (from session trace):");

        // Track which trace errors we've already surfaced so the trailing server-only list doesn't
        // duplicate them verbatim. Trace errors are the localized per-table messages; the server
        // list is usually a superset that adds callstack/transaction details.
        var surfaced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in tableErrors.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var label = kv.Key.Length == 0 ? "(unknown table)" : kv.Key;
            sb.Append("  ").Append(label).Append(':');
            foreach (var err in kv.Value)
            {
                sb.AppendLine();
                sb.Append("    ").Append(err.Trim().Replace("\n", "\n    "));
                surfaced.Add(err.Trim());
            }
            sb.AppendLine();
        }

        // Anything in serverErrors that wasn't already surfaced above goes here so we don't lose
        // transaction-level context (e.g. "The current action was cancelled...").
        var extras = serverErrors
            .Select(e => e.Trim())
            .Where(e => !surfaced.Contains(e))
            .ToList();
        if (extras.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Additional server messages:");
            foreach (var e in extras)
            {
                sb.Append("  ").Append(e);
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }
}
