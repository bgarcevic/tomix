using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Session;

/// <summary>The result of a save a live session refused because its source changed outside it,
/// or could not make because its server is gone (#351).</summary>
public static class SourceChangedFailure
{
    public const string Code = "TOMIX_SESSION_STALE";

    public const string UnavailableCode = "TOMIX_SESSION_SOURCE_UNAVAILABLE";

    public static TomixResult<T> Result<T>(ModelSourceChangedException ex)
        => TomixResult<T>.Fail(
            Code,
            $"{ex.Message} Saving would overwrite those changes.",
            exitCode: 1,
            ex.CanReload
                ? "Run 'reload' to take the version on disk (unsaved changes are lost), or 'save --force' to overwrite it with the session's."
                : "Run 'save --force' to overwrite the server's model with the session's, or close the session and connect again to take the server's (unsaved changes are lost).");

    public static TomixResult<T> Result<T>(ModelSourceUnavailableException ex)
        => TomixResult<T>.Fail(
            UnavailableCode,
            ex.Message,
            exitCode: 1,
            "The session's changes are still in memory: write them to files with 'save -o <folder>'.");
}
