using Tomix.App.State;

namespace Tomix.App.Mutations;

/// <summary>
/// The stateful stores a mutation needs: the staging store for <c>--stage</c>/<c>--revert</c>
/// working copies and a call-time session read for the sync target. Threaded from the
/// composition root so mutations never construct filesystem-backed state themselves.
/// </summary>
public sealed record MutationStores(StagingStore Staging, Func<CliConnectionState?> ResolveSession)
{
    // M2 transitional: ambient construction for callers not yet wired from the composition
    // root. Deleted together with the parameterless store ctors once the CLI passes stores.
    public static MutationStores Ambient() => new(new StagingStore(), () => new CliStateStore().LoadCurrentSession());
}
