using Tomix.App.State;
using System.Text.Json.Serialization;

namespace Tomix.App.Connect;

public sealed record ConnectShowResult(
    bool Active,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    CliConnectionState? Connection);

public sealed record ConnectSetResult(
    bool Active,
    CliConnectionState Connection);

public sealed record ConnectClearResult(bool Cleared);

public sealed record ConnectRecentListResult(IReadOnlyList<RecentConnection> Connections);
