using Tomix.Core.Rules;

namespace Tomix.Core.Models;

/// <summary>
/// What the target server reports about the deployed model's objects once a deploy has run.
/// The server accepting the TMSL only means the metadata was valid; an object can still be in
/// an error state (a measure whose DAX does not resolve, an RLS filter that fails), which a
/// deploy must not report as a clean success. <see cref="Issues"/> lists every object that is
/// not ready: <see cref="RuleSeverity.Error"/> for semantic, evaluation and dependency errors,
/// <see cref="RuleSeverity.Warning"/> for incomplete objects, and <see cref="RuleSeverity.Info"/>
/// for objects that are only unprocessed — the normal state after a metadata-only deploy.
/// </summary>
public sealed record DeployValidation(IReadOnlyList<DeployedObjectIssue> Issues)
{
    public int Errors => Issues.Count(issue => issue.Severity == RuleSeverity.Error);

    public int Warnings => Issues.Count(issue => issue.Severity == RuleSeverity.Warning);

    /// <summary>Objects that hold no data or need recalculation; a refresh clears them.</summary>
    public int Unprocessed => Issues.Count(issue => issue.Severity == RuleSeverity.Info);
}

/// <summary>
/// One deployed object that is not ready. <paramref name="ObjectType"/> is the TOM kind
/// (<c>Measure</c>, <c>CalculatedColumn</c>, <c>Partition</c>, ...), <paramref name="ObjectName"/>
/// names it the way <c>validate</c> does (<c>Sales[Total]</c>), <paramref name="State"/> is the
/// server's object state, and <paramref name="Message"/> the server's error text, when it gave one.
/// </summary>
public sealed record DeployedObjectIssue(
    RuleSeverity Severity,
    string ObjectType,
    string ObjectName,
    string State,
    string? Message);
