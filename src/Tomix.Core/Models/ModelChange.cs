using System.Text.Json.Serialization;

namespace Tomix.Core.Models;

/// <summary>What happened to one object in a committed transaction.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModelChangeKind>))]
public enum ModelChangeKind
{
    [JsonStringEnumMemberName("added")]
    Added,
    [JsonStringEnumMemberName("removed")]
    Removed,
    [JsonStringEnumMemberName("modified")]
    Modified,
    [JsonStringEnumMemberName("renamed")]
    Renamed,
    [JsonStringEnumMemberName("moved")]
    Moved
}

/// <summary>What produced a committed transaction.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChangeOriginKind>))]
public enum ChangeOriginKind
{
    /// <summary>A client request (a command in the shell, an RPC call, an MCP tool).</summary>
    [JsonStringEnumMemberName("apply")]
    Apply,
    [JsonStringEnumMemberName("undo")]
    Undo,
    [JsonStringEnumMemberName("redo")]
    Redo,
    /// <summary>The session re-read its source; IDs may have been reassigned.</summary>
    [JsonStringEnumMemberName("reload")]
    Reload,
    /// <summary>An approved change set (#370).</summary>
    [JsonStringEnumMemberName("changeSet")]
    ChangeSet
}

/// <summary>The client and kind of operation behind a <see cref="ModelChangeBatch"/>.</summary>
/// <param name="Client">The attached client that made the change, for example <c>shell</c> or
/// <c>mcp-1</c>; <c>null</c> when the session host made it itself.</param>
/// <param name="Kind">The kind of operation.</param>
public sealed record ChangeOrigin(string? Client, ChangeOriginKind Kind);

/// <summary>
/// One object touched by a committed transaction. Events name what changed but carry no values:
/// clients read values at the batch's version (ADR 0001 §7).
/// </summary>
/// <param name="Id">The object's session ID.</param>
/// <param name="ObjectKind">The object's kind.</param>
/// <param name="Change">What happened to it.</param>
/// <param name="Path">The object's path after the transaction; for <see cref="ModelChangeKind.Removed"/>,
/// its path before.</param>
/// <param name="OldPath">The path before a <see cref="ModelChangeKind.Renamed"/> or
/// <see cref="ModelChangeKind.Moved"/> change; otherwise <c>null</c>.</param>
/// <param name="Properties">The property keys that changed, as the property catalog names them,
/// for <see cref="ModelChangeKind.Modified"/>; otherwise <c>null</c>.</param>
public sealed record ModelChange(
    ObjectId Id,
    ModelObjectKind ObjectKind,
    ModelChangeKind Change,
    string Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? OldPath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Properties = null);

/// <summary>
/// Everything one committed transaction changed: the payload of the <c>model.changed</c> event.
/// There is exactly one batch per committed transaction, whether it touched one object or many.
/// </summary>
/// <param name="Version">The session version after the transaction. Versions are monotonic and
/// gap-free, so a client that sees a gap resyncs instead of replaying.</param>
/// <param name="Transaction">The transaction's ID, for example <c>t17</c>. Undo and redo batches
/// name the transaction they reverse or reapply.</param>
/// <param name="Origin">Who made the change and how.</param>
/// <param name="Changes">The objects touched, cascades included.</param>
public sealed record ModelChangeBatch(
    long Version,
    string Transaction,
    ChangeOrigin Origin,
    IReadOnlyList<ModelChange> Changes);
