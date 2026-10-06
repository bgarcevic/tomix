namespace Tomix.Core.Models;

/// <summary>
/// Thrown when a live session would save over changes made to its source outside the session:
/// the files were edited, replaced or checked out since the session opened or last saved
/// (<see cref="SessionState.Stale"/>). The caller reloads, or keeps its own changes with
/// <see cref="IExternalChangeSession.KeepChanges"/> and saves again.
/// </summary>
public sealed class ModelSourceChangedException : Exception
{
    public ModelSourceChangedException(string source)
        : base($"{source} changed outside the session since it was opened or last saved.")
    {
        SourcePath = source;
    }

    /// <summary>The file or folder that changed.</summary>
    public string SourcePath { get; }
}
