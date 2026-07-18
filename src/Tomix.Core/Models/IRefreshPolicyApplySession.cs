namespace Tomix.Core.Models;

/// <summary>
/// Optional server-side capability: a session that can apply a table's incremental refresh
/// policy over XMLA. Split from <see cref="IModelRefreshSession"/> so handlers discover
/// support with a type test instead of a default interface member that throws at call time.
/// </summary>
public interface IRefreshPolicyApplySession
{
    /// <summary>
    /// Applies a table's incremental refresh policy on the server: diffs the expected partition
    /// scheme for the effective date against the existing partitions and creates/merges/drops
    /// as needed. Request.Refresh=false bootstraps partition definitions without loading data.
    /// </summary>
    Task<RefreshPolicyApplyResult> ApplyRefreshPolicyAsync(
        RefreshPolicyApplyRequest request,
        CancellationToken cancellationToken);
}
