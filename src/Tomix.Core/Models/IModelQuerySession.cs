namespace Tomix.Core.Models;

/// <summary>
/// Session capability that executes a DAX (<c>EVALUATE</c>) or DMV (<c>SELECT ... FROM $SYSTEM....</c>)
/// query against a live model and returns the rowset. Mirrors <see cref="IModelRefreshSession"/>:
/// only live server sessions implement it; file/folder sessions cannot evaluate queries.
/// </summary>
public interface IModelQuerySession
{
    Task<ModelQueryResult> ExecuteQueryAsync(ModelQueryRequest request, CancellationToken cancellationToken);
}

/// <param name="Query">The query text, sent to the server as-is.</param>
/// <param name="Parameters">Optional named parameters referenced as <c>@name</c> in DAX. Values are passed as strings.</param>
/// <param name="MaxRows">Client-side row cap. Implementations read one extra row to detect truncation.</param>
public sealed record ModelQueryRequest(
    string Query,
    IReadOnlyDictionary<string, string>? Parameters = null,
    int? MaxRows = null);

/// <param name="Name">Column name exactly as returned by the server (e.g. <c>Sales[Amount]</c> or <c>[Total Sales]</c>).</param>
/// <param name="Type">Normalized type name: string, int64, double, decimal, boolean, dateTime, or object.</param>
public sealed record QueryColumn(string Name, string Type);

/// <summary>
/// Query rowset. Cell values are restricted to <see cref="string"/>, <see cref="long"/>,
/// <see cref="double"/>, <see cref="decimal"/>, <see cref="bool"/>, <see cref="DateTime"/>,
/// or null (DAX BLANK); implementations must map anything else to a string.
/// </summary>
public sealed record ModelQueryResult(
    string Server,
    string Database,
    IReadOnlyList<QueryColumn> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    bool Truncated,
    long DurationMs);
