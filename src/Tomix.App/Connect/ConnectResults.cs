using System.Text.Json.Serialization;
using Tomix.App.State;

namespace Tomix.App.Connect;

// `Connection` carries the report-label cache for renderers, but the serialized `connection` is a
// projection without it: those fields are an internal display optimization and one of them is an
// absolute path inside the user's profile. Keeping the JSON contract unchanged is deliberate.

public sealed record ConnectShowResult(
    bool Active,
    [property: JsonIgnore]
    CliConnectionState? Connection,
    /// <summary>
    /// For a Power BI Desktop (<c>localhost:&lt;port&gt;</c>) session, whether that instance is
    /// still listening; null for every other kind of connection.
    /// </summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Reachable = null,
    /// <summary>
    /// The report an unreachable Desktop session was last known to serve, for display only. Not
    /// serialized, matching the rest of the report-label cache.
    /// </summary>
    [property: JsonIgnore]
    string? LastReportName = null)
{
    [JsonPropertyName("connection")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CliConnectionState? PublicConnection => Connection?.ToPublic();

    /// <summary>The session file that holds (or would hold) the active connection.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ConnectSessionInfo? Session { get; init; }
}

/// <param name="Kind"><c>directory</c>, <c>named</c> (<c>TOMIX_SESSION</c>), or legacy <c>pid</c>.</param>
/// <param name="Scope">The repository, worktree, or folder a directory session is bound to.</param>
public sealed record ConnectSessionInfo(
    string Id,
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Scope,
    string Path);

public sealed record ConnectSetResult(
    bool Active,
    [property: JsonIgnore]
    CliConnectionState Connection)
{
    [JsonPropertyName("connection")]
    public CliConnectionState PublicConnection => Connection.ToPublic();
}

/// <param name="Removed">With <c>--clear --all</c>, how many session files were deleted.</param>
public sealed record ConnectClearResult(
    bool Cleared,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Removed = null);

public sealed record ConnectRecentListResult(IReadOnlyList<RecentConnection> Connections);
