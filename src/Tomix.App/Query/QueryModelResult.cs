using Tomix.Core.Models;

namespace Tomix.App.Query;

/// <summary>
/// JSON-shaped query result: property order defines the documented <c>--output-format json</c>
/// contract (additive changes only). Cell values follow <see cref="ModelQueryResult"/>'s
/// primitive restrictions; blank serializes as JSON <c>null</c>.
/// </summary>
public sealed record QueryModelResult(
    string Server,
    string Database,
    IReadOnlyList<QueryColumn> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    int RowCount,
    bool Truncated,
    long DurationMs);
