using System.Text.Json.Serialization;
using Tomix.App.State;

namespace Tomix.App.Session;

public sealed record SessionShowResult(
    string SessionId,
    string Kind,
    string Path,
    bool Exists,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    CliConnectionState? Active);

public sealed record SessionListResult(IReadOnlyList<SessionFileInfo> Sessions);

public sealed record SessionClearResult(bool Cleared);

public sealed record SessionPruneResult(int Removed, bool DryRun);
