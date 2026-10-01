using System.Text.Json.Serialization;

namespace Tomix.Core.Models;

/// <summary>The lifecycle state of a live model session (ADR 0001 §1).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SessionState>))]
public enum SessionState
{
    /// <summary>The current undo position is the save point.</summary>
    [JsonStringEnumMemberName("clean")]
    Clean,

    /// <summary>The current undo position is not the save point. Undoing back to it makes the session clean.</summary>
    [JsonStringEnumMemberName("dirty")]
    Dirty,

    /// <summary>A save is running.</summary>
    [JsonStringEnumMemberName("saving")]
    Saving,

    /// <summary>The source changed underneath the session; the client must reload, keep its own
    /// changes (a forced save), or later merge (#374).</summary>
    [JsonStringEnumMemberName("stale")]
    Stale,

    /// <summary>The session is closed and its model released.</summary>
    [JsonStringEnumMemberName("closed")]
    Closed
}

/// <summary>A transition of <see cref="ILiveModelSession.State"/>.</summary>
public sealed record SessionStateChange(SessionState Previous, SessionState Current, long Version);
