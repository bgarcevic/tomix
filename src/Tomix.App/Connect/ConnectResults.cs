using System.Text.Json.Serialization;
using Tomix.App.State;

namespace Tomix.App.Connect;

// `Connection` carries the report-label cache for renderers, but the serialized `connection` is a
// projection without it: those fields are an internal display optimization and one of them is an
// absolute path inside the user's profile. Keeping the JSON contract unchanged is deliberate.

public sealed record ConnectShowResult(
    bool Active,
    [property: JsonIgnore]
    CliConnectionState? Connection)
{
    [JsonPropertyName("connection")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CliConnectionState? PublicConnection => Connection?.WithoutReportCache();
}

public sealed record ConnectSetResult(
    bool Active,
    [property: JsonIgnore]
    CliConnectionState Connection)
{
    [JsonPropertyName("connection")]
    public CliConnectionState PublicConnection => Connection.WithoutReportCache();
}

public sealed record ConnectClearResult(bool Cleared);

public sealed record ConnectRecentListResult(IReadOnlyList<RecentConnection> Connections);
