using System.Diagnostics;
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
/// </summary>
public static class TomModelQueryExecutor
{
    public static async Task<ModelQueryResult> ExecuteAsync(
        string connectionString,
        ModelReference reference,
        string databaseName,
        IAccessTokenProvider? tokenProvider,
        ModelQueryRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = new AdomdConnection(connectionString);

        if (!reference.IsLocalInstance)
        {
            if (tokenProvider is null)
                throw new AuthenticationRequiredException("Not authenticated. Run 'tx auth login'.");

            var token = await tokenProvider.GetTokenAsync(reference.Value, cancellationToken).ConfigureAwait(false);
            connection.AccessToken = new AsAccessToken(token.Token, token.ExpiresOn.UtcDateTime);
            connection.OnAccessTokenExpired = _ =>
            {
                var refreshed = tokenProvider.GetTokenAsync(reference.Value, cancellationToken)
                    .ConfigureAwait(false).GetAwaiter().GetResult();
                return new AsAccessToken(refreshed.Token, refreshed.ExpiresOn.UtcDateTime);
            };
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

        var sw = Stopwatch.StartNew();
        try
        {
            return await Task.Run(() =>
            {
                connection.Open();
                command.Connection = connection;
                using var reader = command.ExecuteReader();

                var columns = ReadColumns(reader);
                var rows = new List<IReadOnlyList<object?>>();
                var truncated = false;

                while (reader.Read())
                {
                    if (request.MaxRows is { } cap && rows.Count >= cap)
                    {
                        truncated = true;
                        break;
                    }
                    rows.Add(MapRow(reader, columns.Count));
                }

                sw.Stop();
                return new ModelQueryResult(
                    reference.Value, databaseName, columns, rows, truncated, sw.ElapsedMilliseconds);
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
