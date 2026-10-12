using System.Diagnostics;
using System.Security;
using Microsoft.AnalysisServices.AdomdClient;
using Tomix.Core.Authentication;
using Tomix.Core.Models;
using AsAccessToken = Microsoft.AnalysisServices.AccessToken;

namespace Tomix.Provider.Tom;

/// <summary>
/// Executes a DAX or DMV query over a dedicated ADOMD connection and maps the rowset to
/// Core types. Mirrors <see cref="TomModelRefresher"/>: provider-side helper invoked by
/// <c>TomServerModelSession.ExecuteQueryAsync</c>. ADOMD cannot share the AMO connection,
/// so a fresh <see cref="AdomdConnection"/> is opened per query using the same endpoint
/// and token plumbing as <see cref="TomServerModelProvider.OpenAsync"/>.
/// <para>
/// Perf options: with <c>Trace</c> a <see cref="TomQueryTraceSink"/> captures
/// server timings; with <c>ClearCache</c> the model cache is flushed (and warmed)
/// before each run; with <c>Runs &gt; 1</c> the query is repeated for benchmarking. All perf
/// features are best-effort — the rowset is always returned even when tracing/clear-cache is
/// unavailable (they need write access to the model).
/// </para>
/// </summary>
public static class TomModelQueryExecutor
{
    /// <summary>
    /// How long to wait for a run's <c>QueryEnd</c> trace event. Fabric XMLA delivers trace events
    /// in batches several seconds after the query finishes (~8 s observed), so this is generous;
    /// the wait ends as soon as the event lands and only runs out when tracing is broken.
    /// </summary>
    private static readonly TimeSpan RunEventTimeout = TimeSpan.FromSeconds(30);

    public static async Task<ModelQueryResult> ExecuteAsync(
        string connectionString,
        ModelReference reference,
        string databaseName,
        string databaseId,
        IAccessTokenProvider? tokenProvider,
        ModelQueryRequest request,
        TextWriter? traceWriter,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A unique Application Name lets the trace filter and correlate this query's events even
        // where the service reports a SessionID other than the one ADOMD holds (see TomQueryTraceSink).
        var traced = request.Trace;
        var applicationName = $"tomix-query-{Guid.NewGuid():N}";
        if (traced)
            connectionString = $"{connectionString};Application Name={applicationName}";

        using var connection = new AdomdConnection(connectionString);

        Func<AsAccessToken>? tokenFactory = null;
        if (reference.RequiresAccessToken)
        {
            if (tokenProvider is null)
                throw new AuthenticationRequiredException("Not authenticated. Run 'tx auth login'.");

            var token = await tokenProvider.GetTokenAsync(ModelReference.DataSourceOf(reference.Value), cancellationToken).ConfigureAwait(false);
            connection.AccessToken = new AsAccessToken(token.Token, token.ExpiresOn.UtcDateTime);
            // Shared by the ADOMD connection's expiry callback and the trace connection.
            tokenFactory = () =>
            {
                var refreshed = tokenProvider.GetTokenAsync(ModelReference.DataSourceOf(reference.Value), cancellationToken)
                    .ConfigureAwait(false).GetAwaiter().GetResult();
                return new AsAccessToken(refreshed.Token, refreshed.ExpiresOn.UtcDateTime);
            };
            connection.OnAccessTokenExpired = _ => tokenFactory();
        }

        using var command = new AdomdCommand(request.Query);

        if (request.Parameters is { Count: > 0 })
        {
            foreach (var (name, value) in request.Parameters)
                command.Parameters.Add(new AdomdParameter(name.TrimStart('@'), value));
        }

        // ADOMD is a synchronous API; Cancel() aborts a running ExecuteReader from another
        // thread, which is how Ctrl+C interrupts a long query.
        using var cancelRegistration = cancellationToken.Register(() =>
        {
            try { command.Cancel(); }
            catch { /* connection may already be closed */ }
        });

        var runs = Math.Max(1, request.Runs);

        try
        {
            return await Task.Run(() =>
            {
                connection.Open();
                command.Connection = connection;

                // Attach a trace only when timings are requested. Best-effort: a null sink
                // (no write access) means the query still runs, just without server timings.
                string? traceError = null;
                using var sink = traced
                    ? TomQueryTraceSink.Attach(connection, connectionString, applicationName, tokenFactory, traceWriter, out traceError)
                    : null;

                List<QueryColumn> columns = [];
                List<IReadOnlyList<object?>> rows = [];
                var truncated = false;
                var runResults = new List<QueryRun>(runs);
                string? cacheClearError = null;

                for (var run = 1; run <= runs; run++)
                {
                    // A run is only "cold" if the cache actually cleared. A clear that failed (no
                    // write access, shared capacity) leaves the cache warm, and the result says why.
                    var cold = false;
                    if (request.ClearCache)
                    {
                        var error = ClearCache(connection, databaseId);
                        cold = error is null;
                        cacheClearError ??= error;
                    }

                    sink?.StartRun();
                    var runStopwatch = Stopwatch.StartNew();
                    using (var reader = command.ExecuteReader())
                    {
                        if (run == 1)
                        {
                            columns = ReadColumns(reader);
                            while (reader.Read())
                            {
                                if (request.MaxRows is { } cap && rows.Count >= cap)
                                {
                                    truncated = true;
                                    break;
                                }
                                rows.Add(MapRow(reader, columns.Count));
                            }
                        }
                        else
                        {
                            DrainReader(reader, request.MaxRows);
                        }
                    }
                    runStopwatch.Stop();

                    var timings = sink is { Active: true }
                        // After one miss the trace is evidently not delivering; don't stall every run.
                        ? sink.WaitForRun(traceError is not null ? TimeSpan.FromSeconds(2) : RunEventTimeout)
                        : null;
                    if (sink is { Active: true } && timings is null && traceError is null)
                    {
                        // The trace is live but this run's QueryEnd never arrived: say so rather
                        // than leave the timings silently null.
                        traceError =
                            $"no QueryEnd trace event arrived for run {run}" +
                            (sink.ForeignEventCount > 0
                                ? $" ({sink.ForeignEventCount} trace event(s) from other sessions were ignored)."
                                : ".");
                    }
                    runResults.Add(new QueryRun(run, cold, runStopwatch.ElapsedMilliseconds, timings));
                }

                return new ModelQueryResult(
                    reference.Value,
                    databaseName,
                    columns,
                    rows,
                    truncated,
                    runResults[0].ClientMs,
                    runResults,
                    cacheClearError,
                    traceError);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (AdomdException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Query was cancelled.", ex, cancellationToken);
        }
        catch (AdomdException ex)
        {
            // Never leak ADOMD types past the provider boundary; the App handler maps
            // InvalidOperationException to TOMIX_QUERY_FAILED.
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    /// <summary>
    /// Clears the model cache (database-scoped) then runs a marked warm-up query so the calculation
    /// script re-evaluates and the following cold run isn't polluted by one-time init. Best-effort:
    /// clearing the cache needs write access to the model, so a failure leaves the cache warm.
    /// Returns null when the cache was cleared (so the run can be labeled cold), else why not.
    /// </summary>
    private static string? ClearCache(AdomdConnection connection, string databaseId)
    {
        try
        {
            var xmla =
                "<Batch xmlns=\"http://schemas.microsoft.com/analysisservices/2003/engine\">" +
                "<ClearCache><Object><DatabaseID>" +
                SecurityElement.Escape(databaseId) +
                "</DatabaseID></Object></ClearCache></Batch>";
            using (var clear = new AdomdCommand(xmla, connection))
                clear.ExecuteNonQuery();

            using var warmup = new AdomdCommand(
                $"EVALUATE /* {TomQueryTraceSink.InternalMarker} */ ROW(\"tomix\", 0)", connection);
            warmup.ExecuteNonQuery();
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ServerReason(ex.Message);
        }
    }

    /// <summary>
    /// The first line of a server error, without the <c>&lt;euii&gt;</c> markers Power BI wraps
    /// identities in or the "Technical Details" block that follows.
    /// </summary>
    internal static string ServerReason(string message)
    {
        var line = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        return line.Replace("<euii>", "", StringComparison.Ordinal).Replace("</euii>", "", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the database named in <paramref name="connectionString"/> answers a query. A caller
    /// with read (Build) permission only can query a database that AMO does not list, because
    /// listing databases needs write access; this tells that caller apart from a wrong name.
    /// </summary>
    internal static async Task<bool> CanQueryAsync(
        string connectionString,
        ModelReference reference,
        IAccessTokenProvider? tokenProvider,
        CancellationToken cancellationToken)
    {
        using var connection = new AdomdConnection(connectionString);
        if (reference.RequiresAccessToken)
        {
            if (tokenProvider is null)
                return false;

            var token = await tokenProvider.GetTokenAsync(ModelReference.DataSourceOf(reference.Value), cancellationToken).ConfigureAwait(false);
            connection.AccessToken = new AsAccessToken(token.Token, token.ExpiresOn.UtcDateTime);
        }

        try
        {
            return await Task.Run(() =>
            {
                connection.Open();
                using var probe = new AdomdCommand($"EVALUATE /* {TomQueryTraceSink.InternalMarker} */ ROW(\"tomix\", 0)", connection);
                probe.ExecuteNonQuery();
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        // A database that does not exist fails in several ways (an ADOMD error, or an HTTP 404
        // from the Power BI endpoint); any of them means it cannot be queried.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static void DrainReader(AdomdDataReader reader, int? maxRows)
    {
        var read = 0;
        while (reader.Read())
        {
            if (maxRows is { } cap && read >= cap)
                break;
            for (var i = 0; i < reader.FieldCount; i++)
                _ = reader.GetValue(i);
            read++;
        }
    }

    private static List<QueryColumn> ReadColumns(AdomdDataReader reader)
    {
        var columns = new List<QueryColumn>(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
            columns.Add(new QueryColumn(reader.GetName(i), NormalizeTypeName(reader.GetFieldType(i))));
        return columns;
    }

    private static string NormalizeTypeName(Type? type) => Type.GetTypeCode(type) switch
    {
        TypeCode.String => "string",
        TypeCode.SByte or TypeCode.Byte or TypeCode.Int16 or TypeCode.UInt16
            or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 => "int64",
        TypeCode.Single or TypeCode.Double => "double",
        TypeCode.Decimal => "decimal",
        TypeCode.Boolean => "boolean",
        TypeCode.DateTime => "dateTime",
        _ => "object"
    };

    private static object?[] MapRow(AdomdDataReader reader, int fieldCount)
    {
        var cells = new object?[fieldCount];
        for (var i = 0; i < fieldCount; i++)
            cells[i] = MapCell(reader.GetValue(i));
        return cells;
    }

    /// <summary>
    /// Restricts cell values to the Core-safe primitives documented on
    /// <see cref="ModelQueryResult"/>; integral types widen to <see cref="long"/>,
    /// anything unrecognized becomes its string representation.
    /// </summary>
    private static object? MapCell(object? value) => value switch
    {
        null or DBNull => null,
        string or long or double or decimal or bool or DateTime => value,
        sbyte or byte or short or ushort or int or uint => Convert.ToInt64(value),
        ulong u => u <= long.MaxValue ? (long)u : (object)u.ToString(),
        float f => (double)f,
        _ => value.ToString()
    };
}
