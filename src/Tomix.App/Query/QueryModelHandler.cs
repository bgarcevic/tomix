using Tomix.App.State;
using Tomix.Core.Authentication;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Query;

/// <summary>
/// Resolves the query target (primary if remote, else the remote workspace-mode secondary),
/// opens a query-capable session, and executes the DAX/DMV query.
/// Mirrors <see cref="Refresh.RefreshModelHandler"/>. Validation is a leading-keyword
/// pre-check only (EVALUATE/DEFINE/SELECT); the server remains the authority on syntax.
/// </summary>
public sealed class QueryModelHandler
{
    private static readonly string[] ValidLeadingKeywords = ["DEFINE", "EVALUATE", "SELECT"];

    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly Func<CliConnectionState?> _resolveSession;

    public QueryModelHandler(IEnumerable<IModelProvider> providers)
        : this(providers, () => new CliStateStore().LoadCurrentSession())
    {
    }

    public QueryModelHandler(IEnumerable<IModelProvider> providers, Func<CliConnectionState?> resolveSession)
    {
        _providers = providers.ToList();
        _resolveSession = resolveSession;
    }

    /// <param name="traceWriter">Optional raw XMLA trace dump sink (from <c>--trace &lt;path&gt;</c>),
    /// threaded to the provider like <see cref="Refresh.RefreshModelHandler"/> so the App layer stays
    /// free of console/Spectre concerns. Null disables the raw dump.</param>
    public async Task<TomixResult<QueryModelResult>> HandleAsync(
        QueryModelRequest request,
        TextWriter? traceWriter,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return TomixResult<QueryModelResult>.Fail(
                "TOMIX_QUERY_REQUIRED",
                "No query to execute.",
                exitCode: 2,
                hint: "Pass -q \"EVALUATE ...\" or --file query.dax, or pipe a query on stdin.");

        if (!request.NoValidate)
        {
            var keyword = FirstSignificantToken(request.Query);
            if (!ValidLeadingKeywords.Contains(keyword, StringComparer.OrdinalIgnoreCase))
                return TomixResult<QueryModelResult>.Fail(
                    "TOMIX_QUERY_INVALID",
                    keyword.Length == 0
                        ? "The query contains no statement."
                        : $"The query starts with '{keyword}', which is not a query statement.",
                    exitCode: 2,
                    hint: "DAX queries must start with EVALUATE or DEFINE; DMV queries with SELECT. Use --no-validate to send the text as-is.");
        }

        var target = ResolveTarget(request);
        if (target is null || !target.IsRemote)
            return TomixResult<QueryModelResult>.Fail(
                "TOMIX_QUERY_NO_REMOTE_TARGET",
                "No live model to query. Queries execute on a deployed model or a local instance, not on TMDL/BIM files.",
                exitCode: 2,
                hint: "Use -s <workspace> -d <model>, connect to a local instance with -s localhost:<port>, or deploy the local model first ('tx deploy').");

        var provider = _providers.FirstOrDefault(p => p.CanOpen(target));
        if (provider is null)
            return TomixResult<QueryModelResult>.Fail(
                "TOMIX_NO_PROVIDER",
                $"No provider can open remote endpoint: {target.Value}",
                exitCode: 2);

        try
        {
            await using var session = await provider.OpenAsync(target, cancellationToken).ConfigureAwait(false);
            if (session is not IModelQuerySession querySession)
                return TomixResult<QueryModelResult>.Fail(
                    "TOMIX_QUERY_UNSUPPORTED",
                    $"Provider session does not support queries: {target.Value}",
                    exitCode: 2,
                    hint: "Queries are only supported on live models connected via XMLA (-s <workspace> -d <model>).");

            var result = await querySession.ExecuteQueryAsync(
                new ModelQueryRequest(
                    request.Query,
                    request.Parameters,
                    request.Limit,
                    Trace: request.Trace,
                    Plan: request.Plan,
                    ClearCache: request.Cold,
                    Runs: request.Runs < 1 ? 1 : request.Runs),
                traceWriter,
                cancellationToken).ConfigureAwait(false);

            return TomixResult<QueryModelResult>.Ok(new QueryModelResult(
                result.Server,
                result.Database,
                result.Columns,
                result.Rows,
                result.Rows.Count,
                result.Truncated,
                result.DurationMs,
                Timings: result.Runs is { Count: > 0 } ? result.Runs[0].Timings : null,
                Plans: result.Plans,
                Benchmark: QueryBenchmark.Compute(result.Runs)));
        }
        catch (AuthenticationRequiredException ex)
        {
            return TomixResult<QueryModelResult>.Fail(
                "TOMIX_AUTH_REQUIRED",
                ex.Message,
                exitCode: 1,
                hint: "Run 'tx auth login' to authenticate, or use --auth spn for service principal.");
        }
        catch (InvalidOperationException ex)
        {
            return TomixResult<QueryModelResult>.Fail(
                "TOMIX_QUERY_FAILED",
                ex.Message,
                exitCode: 1,
                hint: "Verify the query syntax and that you have read permissions on the dataset.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var msg = ex.InnerException?.Message ?? ex.Message;
            return TomixResult<QueryModelResult>.Fail(
                "TOMIX_QUERY_FAILED",
                $"Query against '{target.Database ?? target.Value}' failed: {msg}",
                exitCode: 1);
        }
    }

    /// <summary>
    /// Pure target resolution: primary reference if remote, otherwise the workspace-mode
    /// secondary when it is remote, otherwise null. Honors explicit --server/--database.
    /// </summary>
    internal static ModelReference? ResolveTarget(
        QueryModelRequest request,
        ActiveModelResolver resolver)
    {
        var primary = resolver.ResolveReference(request.Model, request.Database, request.Server);
        if (primary.IsRemote)
            return primary;

        var secondary = resolver.ResolveSyncTarget();
        if (secondary is null || !secondary.IsRemote)
            return null;

        return secondary;
    }

    private ModelReference? ResolveTarget(QueryModelRequest request)
        => ResolveTarget(request, new ActiveModelResolver(_resolveSession));

    /// <summary>
    /// Returns the first keyword-like token, skipping whitespace and DAX/DMV comments
    /// (<c>//</c>, <c>--</c>, and <c>/* */</c>). Empty when the query holds no code.
    /// </summary>
    internal static string FirstSignificantToken(string query)
    {
        var i = 0;
        while (i < query.Length)
        {
            var c = query[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < query.Length && query[i + 1] == '/')
            {
                i = SkipToLineEnd(query, i);
                continue;
            }

            if (c == '-' && i + 1 < query.Length && query[i + 1] == '-')
            {
                i = SkipToLineEnd(query, i);
                continue;
            }

            if (c == '/' && i + 1 < query.Length && query[i + 1] == '*')
            {
                var close = query.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0)
                    return string.Empty;
                i = close + 2;
                continue;
            }

            var start = i;
            while (i < query.Length && char.IsLetter(query[i]))
                i++;
            return query[start..i];
        }

        return string.Empty;
    }

    private static int SkipToLineEnd(string query, int index)
    {
        var newline = query.IndexOf('\n', index);
        return newline < 0 ? query.Length : newline + 1;
    }
}
