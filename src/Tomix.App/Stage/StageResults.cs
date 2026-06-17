using System.Text.Json.Serialization;
using Tomix.App.State;

namespace Tomix.App.Stage;

public sealed record StageStatusResult(
    bool Staged,
    string Source,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? WorkingCopy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Serialization,
    bool Workspace,
    int OpCount,
    IReadOnlyList<StagedOp> Ops,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? CreatedUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? UpdatedUtc);

public sealed record StageListResult(IReadOnlyList<StageListEntry> Staged);

public sealed record StageListEntry(
    string Source,
    string WorkingCopy,
    int OpCount,
    DateTimeOffset UpdatedUtc,
    bool Current);

public sealed record StageDiscardResult(int Discarded);

public sealed record StageCommitResult(
    string Source,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? LocalSaved,
    bool RemoteDeployed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Server,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Database,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? DeployDurationMs,
    int OpsCommitted);
