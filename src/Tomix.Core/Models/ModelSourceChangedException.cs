namespace Tomix.Core.Models;

/// <summary>
/// Thrown when a live session would save over changes made to its source outside the session:
/// the files were edited, replaced or checked out, or the server's model was changed, since the
/// session opened or last saved (<see cref="SessionState.Stale"/>). The caller reloads where
/// <see cref="CanReload"/>, or keeps its own changes with
/// <see cref="IExternalChangeSession.KeepChanges"/> and saves again.
/// </summary>
public sealed class ModelSourceChangedException : Exception
{
    public ModelSourceChangedException(string source, bool canReload = true)
        : base($"{source} changed outside the session since it was opened or last saved.")
    {
        SourcePath = source;
        CanReload = canReload;
    }

    /// <summary>The file or folder that changed, or the server model, by name.</summary>
    public string SourcePath { get; }

    /// <summary>Whether the session can read the source again (<see cref="ILiveModelSession.ReloadAsync"/>).</summary>
    public bool CanReload { get; }
}
