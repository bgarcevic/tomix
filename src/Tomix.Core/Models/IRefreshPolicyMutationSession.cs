namespace Tomix.Core.Models;

/// <summary>
/// Optional mutation capability: a session that can read and edit incremental refresh
/// policies. Handlers capability-check with a type test instead of discovering support
/// through <see cref="NotSupportedException"/> at call time.
/// </summary>
public interface IRefreshPolicyMutationSession
{
    /// <summary>
    /// Returns the table's incremental refresh policy with validation issues attached, or null
    /// when the table exists but has no policy. Throws <see cref="ObjectNotFoundException"/>
    /// when the table is missing.
    /// </summary>
    RefreshPolicyInfo? GetRefreshPolicy(string table);

    /// <summary>
    /// Creates or edits the table's incremental refresh policy. Throws
    /// <see cref="RefreshPolicyValidationException"/> when validation errors exist and the
    /// request did not pass Force.
    /// </summary>
    RefreshPolicySetResult SetRefreshPolicy(RefreshPolicySetRequest request);

    ModelObjectMutationResult RemoveRefreshPolicy(string table, bool ifExists = false);
}
