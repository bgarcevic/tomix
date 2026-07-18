using Tomix.Core.Models;

namespace Tomix.App.Mutations;

/// <summary>
/// Capability checks for optional mutation features. The thrown
/// <see cref="NotSupportedException"/> is mapped to <c>TOMIX_MUTATION_UNSUPPORTED</c> by
/// <see cref="MutationRunner"/> and the refresh-policy handlers, preserving the diagnostic
/// contract of the former default interface members.
/// </summary>
internal static class MutationCapabilities
{
    public static IExpressionRewriteSession RequireExpressionRewrites(IModelMutationSession mutator)
        => mutator as IExpressionRewriteSession
            ?? throw new NotSupportedException("This provider does not support expression rewriting.");

    public static IRefreshPolicyMutationSession RequireRefreshPolicies(IModelMutationSession mutator)
        => mutator as IRefreshPolicyMutationSession
            ?? throw new NotSupportedException("This provider does not support refresh policies.");
}
