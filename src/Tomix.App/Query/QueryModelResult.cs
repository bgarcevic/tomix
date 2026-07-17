using Tomix.Core.Models;

namespace Tomix.App.Query;

/// <summary>
/// JSON-shaped query result: property order defines the documented <c>--output-format json</c>
/// contract (additive changes only). Cell values follow <see cref="ModelQueryResult"/>'s
/// primitive restrictions; blank serializes as JSON <c>null</c>.
/// <para>
/// <see cref="Timings"/> (server timings for the first run), <see cref="Plans"/> (logical/physical
/// query plans), and <see cref="Benchmark"/> (multi-run statistics) are null unless the matching
/// perf option (<c>--trace</c>/<c>--plan</c>/<c>--runs</c>) was requested and honored.
/// </para>
/// </summary>
public sealed record QueryModelResult(
    string Server,
    string Database,
    IReadOnlyList<QueryColumn> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    int RowCount,
    bool Truncated,
    long DurationMs,
    QueryTimings? Timings = null,
    IReadOnlyList<QueryPlan>? Plans = null,
    QueryBenchmark? Benchmark = null);
