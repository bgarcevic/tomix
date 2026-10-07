namespace Tomix.Core.Models;

/// <summary>
/// Thrown when a live session cannot reach the server its model lives on, for example because
/// the Power BI Desktop instance it was opened from has closed (#351). The session's model is
/// still in memory, so it can still be written to files.
/// </summary>
public sealed class ModelSourceUnavailableException : Exception
{
    public ModelSourceUnavailableException(string source, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        SourcePath = source;
    }

    /// <summary>The server model that cannot be reached, by name.</summary>
    public string SourcePath { get; }
}
