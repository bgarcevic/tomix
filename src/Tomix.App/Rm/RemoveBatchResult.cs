using System.Text.Json.Serialization;
using Tomix.App.Mutations;

namespace Tomix.App.Rm;

/// <summary>
/// A batch <c>rm</c>. The removed paths serialize as <c>removed</c> once saved, staged, or applied
/// and as <c>wouldRemove</c> for a preview, like the single-object result. <c>targets</c> accounts
/// for every input; <c>cascades</c> lists the structural changes the removal makes to objects
/// outside the batch. <c>reason</c> is <c>would_block</c> on a preview the guards would refuse to
/// save, with <c>blockedBy</c> naming the guards.
/// </summary>
public sealed record RemoveBatchResult(
    IReadOnlyList<RemoveBatchTarget> Targets,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<RemoveBatchCascade>? Cascades = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? BrokenReferences = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? BoundPartitions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Reason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? BlockedBy = null) : MutationResult
{
    public const string BlockedByReferences = "brokenReferences";
    public const string BlockedByCascade = "cascade";
    public const string BlockedByBoundPartitions = "boundPartitions";

    [JsonIgnore]
    public IReadOnlyList<string> RemovedPaths
        => Targets.Where(t => t.Action == RemoveBatchTarget.Remove).Select(t => t.Path!).ToList();

    [JsonPropertyOrder(-2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Removed => Outcome.Applied ? RemovedPaths : null;

    [JsonPropertyOrder(-2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? WouldRemove => Outcome.Previewed ? RemovedPaths : null;
}

/// <summary>One input target: what it resolved to and what the batch does with it. <c>action</c> is
/// <c>remove</c>, <c>subsumed</c> (removed with <c>subsumedBy</c>, another target), or
/// <c>notFound</c> (skipped under <c>--if-exists</c>).</summary>
public sealed record RemoveBatchTarget(
    string Input,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Type,
    string Action,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SubsumedBy = null)
{
    public const string Remove = "remove";
    public const string Subsumed = "subsumed";
    public const string NotFound = "notFound";
}

/// <summary>A structural change the batch makes outside itself, caused by removing <c>target</c>:
/// the object it changed (<c>path</c>, <c>type</c>) and a description. <c>requiresCascade</c> is
/// false only for the removed object's own translations.</summary>
public sealed record RemoveBatchCascade(
    string Target,
    string Path,
    string Type,
    string Description,
    bool RequiresCascade);
