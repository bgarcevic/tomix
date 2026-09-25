using System.Text.Json.Serialization;
using Tomix.App.State;
using Tomix.Core.Models;

namespace Tomix.App.Mutations;

/// <summary>What a mutation command did with its edit. Serialized as the JSON <c>status</c>.</summary>
public enum MutationStatus
{
    /// <summary>Applied in memory only; nothing was written (no <c>--save</c>/<c>--stage</c>).</summary>
    [JsonStringEnumMemberName("preview")] Preview,
    /// <summary><c>--dry-run</c>: the edit was evaluated and discarded.</summary>
    [JsonStringEnumMemberName("dryRun")] DryRun,
    /// <summary>Nothing to change (e.g. <c>--if-not-exists</c> on an existing object).</summary>
    [JsonStringEnumMemberName("unchanged")] Unchanged,
    /// <summary>Recorded in the staged working copy (<c>--stage</c>).</summary>
    [JsonStringEnumMemberName("staged")] Staged,
    /// <summary>Persisted (<c>--save</c>/<c>--save-to</c>).</summary>
    [JsonStringEnumMemberName("saved")] Saved,
    /// <summary>Staged work was discarded (<c>--revert</c>).</summary>
    [JsonStringEnumMemberName("reverted")] Reverted,
}

/// <summary>Where a save landed, so callers know what survives. Serialized as <c>persistence</c>.</summary>
public enum PersistenceKind
{
    /// <summary>A TMDL folder or .bim file on disk.</summary>
    [JsonStringEnumMemberName("file")] File,
    /// <summary>
    /// The model running inside Power BI Desktop. The change lives in Desktop's in-memory model
    /// until the report is saved in Desktop.
    /// </summary>
    [JsonStringEnumMemberName("liveModel")] LiveModel,
    /// <summary>A remote XMLA endpoint (Fabric / Power BI service, Azure Analysis Services).</summary>
    [JsonStringEnumMemberName("service")] Service,
}

/// <summary>Outcome of the workspace-mirror sync that follows a save. Serialized as <c>sync.status</c>.</summary>
public enum SyncStatus
{
    /// <summary>The mutation was not saved, so no sync was attempted.</summary>
    [JsonStringEnumMemberName("notAttempted")] NotAttempted,
    /// <summary>No workspace mirror is configured for this model.</summary>
    [JsonStringEnumMemberName("notConfigured")] NotConfigured,
    /// <summary>A mirror is configured but was not deployed (<c>--no-sync</c>, <c>--save-to</c>, or the provider cannot deploy).</summary>
    [JsonStringEnumMemberName("skipped")] Skipped,
    [JsonStringEnumMemberName("succeeded")] Succeeded,
    [JsonStringEnumMemberName("failed")] Failed,
}

public sealed record SyncOutcome(
    SyncStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Target = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Warning = null)
{
    public static readonly SyncOutcome NotAttempted = new(SyncStatus.NotAttempted);
    public static readonly SyncOutcome NotConfigured = new(SyncStatus.NotConfigured);
}

/// <summary>
/// The model a mutation addressed, for remote/live models: the endpoint, the database as the
/// endpoint knows it, and the friendly name <c>tx connect</c> shows (the Power BI Desktop report).
/// </summary>
public sealed record MutationTarget(
    string Server,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Database,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Model)
{
    /// <summary>Null for local paths; the path is already in <c>savedTo</c> and the command input.</summary>
    public static MutationTarget? For(ModelReference model, CliConnectionState? connection)
    {
        if (!model.IsRemote)
            return null;

        var sameServer = connection is not null
            && string.Equals(connection.Server, model.Value, StringComparison.OrdinalIgnoreCase);
        return new MutationTarget(model.Value, model.Database, sameServer ? connection!.ReportName : null);
    }

    /// <summary>
    /// Combines the target known before opening (from the connection, which carries the friendly
    /// name) with the one a save reports (which knows the database even when the connection named
    /// none, as for a <c>tx connect --local</c> Power BI Desktop session).
    /// </summary>
    public static MutationTarget? Merge(MutationTarget? fromConnection, MutationTarget? fromSave)
        => fromSave is null
            ? fromConnection
            : fromSave with { Model = fromConnection?.Model ?? fromSave.Model };
}

/// <summary>How the shared lifecycle persisted a mutation. Every mutation result is built from one.</summary>
public sealed record MutationOutcome(
    MutationStatus Status,
    string? SavedTo = null,
    PersistenceKind? Persistence = null,
    SyncOutcome? Sync = null,
    MutationTarget? Target = null,
    SaveValidationDelta? Validation = null)
{
    /// <summary>
    /// True when <c>--dry-run</c> was passed, including when there was nothing to change
    /// (status <c>unchanged</c>), so a caller never mistakes a dry run for a live run.
    /// </summary>
    public bool DryRunRequested { get; init; } = Status == MutationStatus.DryRun;

    public static readonly MutationOutcome Preview = new(MutationStatus.Preview);
    public static readonly MutationOutcome DryRun = new(MutationStatus.DryRun);
    public static readonly MutationOutcome Unchanged = new(MutationStatus.Unchanged);
    public static readonly MutationOutcome Staged = new(MutationStatus.Staged);
    public static readonly MutationOutcome Reverted = new(MutationStatus.Reverted);

    public bool Saved => Status == MutationStatus.Saved;

    /// <summary>True when the edit reached disk or the model (saved or staged); results use past-tense keys only then.</summary>
    public bool Applied => Status is MutationStatus.Saved or MutationStatus.Staged;

    /// <summary>True when the edit was evaluated but not kept; results use <c>would*</c> keys then.</summary>
    public bool Previewed => Status is MutationStatus.Preview or MutationStatus.DryRun;

    /// <summary>
    /// True when a workspace sync was attempted (or required) and did not happen. The command
    /// should exit non-zero so CI catches mirror drift, while still rendering the saved result.
    /// </summary>
    public bool SyncFailed => Sync?.Warning is not null;
}

/// <summary>
/// The persistence fields every mutation result carries, so the JSON contract cannot drift
/// between commands: <c>status</c>, <c>dryRun</c>, <c>saved</c> (always a bool), <c>savedTo</c>,
/// <c>persistence</c>, <c>target</c>, <c>sync</c>, and <c>newValidationErrors</c>. Derived
/// records set <see cref="Outcome"/> with an initializer.
/// </summary>
public abstract record MutationResult
{
    [JsonIgnore]
    public MutationOutcome Outcome { get; init; } = MutationOutcome.Preview;

    [JsonPropertyOrder(100)]
    public MutationStatus Status => Outcome.Status;

    [JsonPropertyOrder(101)]
    public bool DryRun => Outcome.DryRunRequested;

    [JsonPropertyOrder(102)]
    public bool Saved => Outcome.Saved;

    /// <summary>The saved folder/file path, or <c>server / database</c> for a remote or live model.</summary>
    [JsonPropertyOrder(103)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SavedTo => Outcome.SavedTo;

    [JsonPropertyOrder(104)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PersistenceKind? Persistence => Outcome.Persistence;

    [JsonPropertyOrder(105)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MutationTarget? Target => Outcome.Target;

    [JsonPropertyOrder(106)]
    public SyncOutcome Sync => Outcome.Sync ?? SyncOutcome.NotAttempted;

    [JsonPropertyOrder(107)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NewValidationErrors => Outcome.Validation?.NewErrorCount;

    /// <summary>The object path under its past-tense key: set only when the edit was saved or staged.</summary>
    protected string? IfApplied(string? path) => Outcome.Applied ? path : null;

    /// <summary>The object path under its <c>would*</c> key: set only for previews and dry runs.</summary>
    protected string? IfPreviewed(string? path) => Outcome.Previewed ? path : null;
}
