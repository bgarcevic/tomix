using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Session;

/// <summary>
/// The session commands of one client of a live session: status, history, undo and redo, and
/// the client's explicit transaction (ADR 0001 §4). A host keeps one instance per client for as
/// long as the client is attached, since it holds the client's open transaction.
/// </summary>
public sealed class LiveSessionHandler
{
    private readonly ILiveModelSession _session;
    private readonly string _client;
    private ILiveSessionLease? _transaction;
    private string? _transactionLabel;

    public LiveSessionHandler(ILiveModelSession session, string client)
    {
        _session = session;
        _client = client;
    }

    /// <summary>True when the client has a transaction open.</summary>
    public bool InTransaction => _transaction is not null;

    /// <summary>True when closing now would lose work: unsaved changes or an open transaction.</summary>
    public bool HasUnsavedWork => _session.IsDirty || InTransaction;

    public TomixResult<SessionStatusResult> Status()
        => TomixResult<SessionStatusResult>.Ok(new SessionStatusResult(
            _session.Reference.Value,
            _session.SourcePath,
            _session.State,
            _session.IsDirty,
            _session.Version,
            _session.History.Count(step => !step.Undone),
            _session.History.Count(step => step.Undone),
            _transaction is null ? null : new SessionTransactionInfo(_transaction.Transaction, _transactionLabel),
            _session.CanReload,
            _session.SourceUnavailable));

    public TomixResult<SessionHistoryResult> History()
        => TomixResult<SessionHistoryResult>.Ok(new SessionHistoryResult(
            _session.History.Select(step => new SessionHistoryEntry(step.Transaction, step.Label, step.Changes.Count, step.Undone)).ToList()));

    public Task<TomixResult<SessionStepResult>> UndoAsync(CancellationToken cancellationToken)
        => StepAsync(undo: true, cancellationToken);

    public Task<TomixResult<SessionStepResult>> RedoAsync(CancellationToken cancellationToken)
        => StepAsync(undo: false, cancellationToken);

    public async Task<TomixResult<SessionStepResult>> BeginAsync(string? label, CancellationToken cancellationToken)
    {
        if (_transaction is not null)
            return TomixResult<SessionStepResult>.Fail(
                "TOMIX_SESSION_TRANSACTION_OPEN",
                $"Transaction {_transaction.Transaction} is already open; transactions do not nest.",
                exitCode: 2,
                "Run 'commit' or 'rollback' first.");

        _transaction = await _session.BeginTransactionAsync(new LiveLeaseOptions(_client, label), cancellationToken);
        _transactionLabel = label;
        return Step("begin", _transaction.Transaction, label, []);
    }

    public async Task<TomixResult<SessionStepResult>> CommitAsync(CancellationToken cancellationToken)
    {
        if (TakeTransaction() is not { } transaction)
            return NoTransaction("commit");

        try
        {
            var batch = await transaction.CommitAsync(cancellationToken);
            return Step("commit", transaction.Transaction, _transactionLabel, batch?.Changes ?? []);
        }
        catch (ObjectDisposedException)
        {
            return Ended(transaction);
        }
    }

    public async Task<TomixResult<SessionStepResult>> RollbackAsync(CancellationToken cancellationToken)
    {
        if (TakeTransaction() is not { } transaction)
            return NoTransaction("rollback");

        try
        {
            await transaction.RollbackAsync(cancellationToken);
            return Step("rollback", transaction.Transaction, _transactionLabel, []);
        }
        catch (ObjectDisposedException)
        {
            return Ended(transaction);
        }
    }

    /// <summary>
    /// Reads the model's files again (#351), for a session whose source changed outside it. Fails
    /// with <c>TOMIX_SESSION_DIRTY</c> when that would discard unsaved changes, unless
    /// <paramref name="discard"/> says to.
    /// </summary>
    public async Task<TomixResult<SessionStepResult>> ReloadAsync(bool discard, CancellationToken cancellationToken)
    {
        if (_transaction is not null)
            return TomixResult<SessionStepResult>.Fail(
                "TOMIX_SESSION_IN_TRANSACTION",
                $"Cannot reload inside transaction {_transaction.Transaction}.",
                exitCode: 2,
                "Run 'commit' or 'rollback' first.");
        if (!_session.CanReload)
            return TomixResult<SessionStepResult>.Fail(
                "TOMIX_SESSION_SOURCE_UNSUPPORTED",
                $"A session on {_session.Reference.Value} cannot reload it; reload works for TMDL folders and .bim files.",
                exitCode: 2,
                "Close the session and open the model again.");
        if (_session.IsDirty && !discard)
            return TomixResult<SessionStepResult>.Fail(
                "TOMIX_SESSION_DIRTY",
                "Reloading would discard the session's unsaved changes and its undo history.",
                exitCode: 1,
                "Pass --discard to reload anyway, or 'save --force' to keep the session's version instead.");

        try
        {
            var batch = await _session.ReloadAsync(_client, cancellationToken);
            return Step("reload", batch.Transaction, null, batch.Changes);
        }
        catch (ModelLoadException ex)
        {
            return TomixResult<SessionStepResult>.Fail(
                "TOMIX_MODEL_LOAD_FAILED",
                ex.Message,
                exitCode: 2,
                "Fix the files and run 'reload' again; the session keeps its model meanwhile.");
        }
    }

    /// <summary>Rolls back the open transaction, if any, before the host closes the session.</summary>
    public async Task<bool> RollbackOpenTransactionAsync(CancellationToken cancellationToken)
    {
        if (TakeTransaction() is not { } transaction)
            return false;

        try
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            // The session already rolled it back.
        }

        return true;
    }

    private async Task<TomixResult<SessionStepResult>> StepAsync(bool undo, CancellationToken cancellationToken)
    {
        var action = undo ? "undo" : "redo";
        if (_transaction is not null)
            return TomixResult<SessionStepResult>.Fail(
                "TOMIX_SESSION_IN_TRANSACTION",
                $"Cannot {action} inside transaction {_transaction.Transaction}.",
                exitCode: 2,
                "Run 'commit' or 'rollback' first.");

        // The step undo reverts is the last applied one; redo reapplies the first undone one.
        var history = _session.History;
        var step = undo ? history.LastOrDefault(s => !s.Undone) : history.FirstOrDefault(s => s.Undone);
        var batch = step is null ? null
            : undo ? await _session.UndoAsync(_client, cancellationToken)
            : await _session.RedoAsync(_client, cancellationToken);
        if (batch is null)
            return TomixResult<SessionStepResult>.Fail(
                undo ? "TOMIX_SESSION_NOTHING_TO_UNDO" : "TOMIX_SESSION_NOTHING_TO_REDO",
                $"Nothing to {action}.",
                exitCode: 1);

        return Step(action, step!.Transaction, step.Label, batch.Changes);
    }

    private ILiveSessionLease? TakeTransaction()
    {
        var transaction = _transaction;
        _transaction = null;
        return transaction;
    }

    private TomixResult<SessionStepResult> Step(string action, string transaction, string? label, IReadOnlyList<ModelChange> changes)
        => TomixResult<SessionStepResult>.Ok(new SessionStepResult(action, transaction, label, changes, _session.Version, _session.IsDirty));

    /// <summary>The session rolled the transaction back on its own: it sat idle too long, or the
    /// session closed.</summary>
    private static TomixResult<SessionStepResult> Ended(ILiveSessionLease transaction)
        => TomixResult<SessionStepResult>.Fail(
            "TOMIX_SESSION_TRANSACTION_ENDED",
            $"Transaction {transaction.Transaction} was rolled back because it was idle too long.",
            exitCode: 1,
            "Its changes are gone; run 'begin' and repeat them.");

    private static TomixResult<SessionStepResult> NoTransaction(string action)
        => TomixResult<SessionStepResult>.Fail(
            "TOMIX_SESSION_NO_TRANSACTION",
            $"No transaction is open to {action}.",
            exitCode: 2,
            "Run 'begin' to open one.");
}
