namespace Tomix.Core.Models;

/// <summary>
/// A live session's model as a lease sees it, when the session watches its source for changes
/// made outside it (#351). Type-test <see cref="ILiveSessionLease.Session"/> for it.
/// </summary>
public interface IExternalChangeSession
{
    /// <summary>True when the source changed outside the session since it was opened, reloaded or
    /// last saved, so a save would overwrite those changes.</summary>
    bool SourceChanged { get; }

    /// <summary>
    /// Keeps the session's model over the changes made outside it: the next save overwrites them
    /// instead of failing with <see cref="ModelSourceChangedException"/>.
    /// </summary>
    void KeepChanges();
}
