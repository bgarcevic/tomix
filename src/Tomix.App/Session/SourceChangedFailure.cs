using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Session;

/// <summary>The result of a save a live session refused because its source changed outside it (#351).</summary>
public static class SourceChangedFailure
{
    public const string Code = "TOMIX_SESSION_STALE";

    public static TomixResult<T> Result<T>(ModelSourceChangedException ex)
        => TomixResult<T>.Fail(
            Code,
            $"{ex.Message} Saving would overwrite those changes.",
            exitCode: 1,
            "Run 'reload' to take the version on disk (unsaved changes are lost), or 'save --force' to overwrite it with the session's.");
}
