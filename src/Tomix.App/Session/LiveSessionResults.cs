using Tomix.Core.Models;

namespace Tomix.App.Session;

/// <summary>What <c>status</c> reports about an open live session.</summary>
/// <param name="Model">The model the session was opened from, as given.</param>
/// <param name="Source">Where the session saves to.</param>
/// <param name="State">The session state: clean, dirty, saving, stale or closed.</param>
/// <param name="Dirty">True when the session has changes that are not saved.</param>
/// <param name="Version">The version of the last committed transaction.</param>
/// <param name="UndoSteps">How many steps <c>undo</c> can revert.</param>
/// <param name="CanReload">Whether <c>reload</c> can read the source again: false for a model on a server.</param>
/// <param name="RedoSteps">How many steps <c>redo</c> can reapply.</param>
/// <param name="Transaction">The open transaction, if any.</param>
/// <param name="SourceUnavailable">True when the session's last look could not reach its source,
/// for example a Power BI Desktop that has closed.</param>
public sealed record SessionStatusResult(
    string Model,
    string Source,
    SessionState State,
    bool Dirty,
    long Version,
    int UndoSteps,
    int RedoSteps,
    SessionTransactionInfo? Transaction,
    bool CanReload = true,
    bool SourceUnavailable = false);

/// <summary>An open explicit transaction.</summary>
/// <param name="Id">The transaction's ID, for example <c>t7</c>.</param>
/// <param name="Label">The name given to <c>begin</c>, if any.</param>
public sealed record SessionTransactionInfo(string Id, string? Label);

/// <summary>The undo history, oldest first; undone steps last, next redo first.</summary>
public sealed record SessionHistoryResult(IReadOnlyList<SessionHistoryEntry> Steps);

/// <param name="Transaction">The transaction's ID.</param>
/// <param name="Label">What ran it: the command line in <c>tx interactive</c>.</param>
/// <param name="Changes">How many changes it made.</param>
/// <param name="Undone">True when it was undone, so <c>redo</c> can reapply it.</param>
public sealed record SessionHistoryEntry(string Transaction, string? Label, int Changes, bool Undone);

/// <summary>The outcome of <c>undo</c>, <c>redo</c>, <c>begin</c>, <c>commit</c> or <c>rollback</c>.</summary>
/// <param name="Action">The command: undo, redo, begin, commit or rollback.</param>
/// <param name="Transaction">The transaction it acted on.</param>
/// <param name="Label">That transaction's label, if any.</param>
/// <param name="Changes">The changes it applied, reverted or committed.</param>
/// <param name="Version">The session version afterwards.</param>
/// <param name="Dirty">True when the session has unsaved changes afterwards.</param>
public sealed record SessionStepResult(
    string Action,
    string Transaction,
    string? Label,
    IReadOnlyList<ModelChange> Changes,
    long Version,
    bool Dirty);

/// <summary>The model an interactive session has open: what <c>open</c> reports and the welcome shows.</summary>
/// <param name="Model">The model as given.</param>
/// <param name="Source">Where the session saves to.</param>
/// <param name="Summary">Its name, compatibility level and object counts.</param>
public sealed record SessionModelInfo(string Model, string Source, ModelSummary Summary);
