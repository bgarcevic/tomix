namespace Tomix.Core.Models;

/// <summary>
/// A model held open in memory that several clients share: the shell, <c>tx serve</c>,
/// <c>tx mcp</c> and the browser UI (ADR 0001, with the concurrency model of ADR 0002).
/// </summary>
/// <remarks>
/// <para>
/// The session itself exposes only reads that need no model access: state, version, events and
/// the published snapshot. Everything that touches the underlying model (mutations, saves,
/// queries, exports) is reachable only through an <see cref="ILiveSessionLease"/>. A session
/// grants one lease at a time, in request order, so the model is never touched concurrently.
/// </para>
/// <para>
/// A lease is a transaction: changes made through it become visible, versioned and undoable as
/// one step when it is committed, and are rolled back when it is disposed without a commit.
/// </para>
/// <para>
/// <see cref="IAsyncDisposable.DisposeAsync"/> closes the session. Only the host process that
/// opened it closes it; attached clients detach instead.
/// </para>
/// </remarks>
public interface ILiveModelSession : IModelSession
{
    /// <summary>The model this session was opened from and saves to.</summary>
    ModelReference Reference { get; }

    SessionState State { get; }

    /// <summary>True when the current undo position is not the save point. A
    /// <see cref="SessionState.Stale"/> session can also be dirty.</summary>
    bool IsDirty { get; }

    /// <summary>The version of the last committed transaction; <c>0</c> right after open.
    /// Increases by exactly one per committed transaction, undo and redo included.</summary>
    long Version { get; }

    bool CanUndo { get; }

    bool CanRedo { get; }

    /// <summary>
    /// The undo history: the steps <see cref="UndoAsync"/> can revert, oldest first, followed by
    /// the steps <see cref="RedoAsync"/> can reapply, next redo first.
    /// </summary>
    IReadOnlyList<LiveHistoryStep> History { get; }

    /// <summary>Raised once per committed transaction, after <see cref="Version"/> has moved.
    /// Handlers run on the committing thread and must not block or lease the session.</summary>
    event EventHandler<ModelChangeBatch>? Changed;

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    event EventHandler<SessionStateChange>? StateChanged;

    /// <summary>
    /// The snapshot at the current <see cref="Version"/>, with object IDs. Snapshots are immutable
    /// and cached per version, so concurrent readers share one. Building the first snapshot of a
    /// version waits for the current lease to end.
    /// </summary>
    Task<LiveModelSnapshot> GetLiveSnapshotAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Waits for exclusive access and opens a transaction. Leases are granted in request order.
    /// When the requesting client holds an open explicit transaction (see
    /// <see cref="BeginTransactionAsync"/>), the lease joins it instead of waiting; a lease
    /// requested while the same asynchronous flow already holds one also joins it.
    /// </summary>
    /// <exception cref="OperationCanceledException">Cancelled before the lease was granted; nothing ran.</exception>
    Task<ILiveSessionLease> LeaseAsync(LiveLeaseOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// Opens an explicit transaction that groups several requests into one undo step. The
    /// returned lease stays granted across requests until it is committed or rolled back; while
    /// it is open, only <see cref="LiveLeaseOptions.Client"/>'s leases join it and every other
    /// client's lease waits. Explicit transactions are flat: beginning one inside another fails.
    /// </summary>
    Task<ILiveSessionLease> BeginTransactionAsync(LiveLeaseOptions options, CancellationToken cancellationToken);

    /// <summary>Reverts the last committed transaction as one step.</summary>
    /// <returns>The batch describing the reversal, or <c>null</c> when there is nothing to undo.</returns>
    Task<ModelChangeBatch?> UndoAsync(string? client, CancellationToken cancellationToken);

    /// <summary>Reapplies the last undone transaction as one step.</summary>
    /// <returns>The batch describing the reapplication, or <c>null</c> when there is nothing to redo.</returns>
    Task<ModelChangeBatch?> RedoAsync(string? client, CancellationToken cancellationToken);
}

/// <summary>One committed transaction in <see cref="ILiveModelSession.History"/>.</summary>
/// <param name="Transaction">The transaction's ID, for example <c>t17</c>.</param>
/// <param name="Label">The <see cref="LiveLeaseOptions.Label"/> it was committed with, if any.</param>
/// <param name="Client">The client that committed it; <c>null</c> for the host.</param>
/// <param name="Changes">The changes it made.</param>
/// <param name="Undone">True when it has been undone and redo would reapply it.</param>
public sealed record LiveHistoryStep(string Transaction, string? Label, string? Client, IReadOnlyList<ModelChange> Changes, bool Undone);

/// <param name="Client">The attached client the lease is for, for example <c>shell</c> or
/// <c>mcp-1</c>; reported in <see cref="ChangeOrigin.Client"/>. <c>null</c> for the host itself.</param>
/// <param name="Label">A short description of the transaction, shown in undo history.</param>
public sealed record LiveLeaseOptions(string? Client = null, string? Label = null);

/// <summary>
/// Exclusive, transactional access to a live session's model. Obtain one from
/// <see cref="ILiveModelSession.LeaseAsync"/>; commit it or dispose it.
/// </summary>
/// <remarks>
/// Disposing without <see cref="CommitAsync"/> rolls back every change made through the lease,
/// so a request that throws partway leaves the model as it found it. A lease that joined an
/// outer transaction rolls back only its own changes, and its commit folds them into the outer
/// transaction instead of publishing a version.
/// </remarks>
public interface ILiveSessionLease : IAsyncDisposable
{
    /// <summary>
    /// The model as an ordinary session: type-test it for the capability interfaces
    /// (<see cref="IModelMutationSession"/>, <see cref="IObjectMoveSession"/>,
    /// <see cref="IModelQuerySession"/>, ...) exactly as handlers do today. Every member throws
    /// <see cref="ObjectDisposedException"/> once the lease has ended, and its own
    /// <see cref="IAsyncDisposable.DisposeAsync"/> does nothing: ending the lease is the lease's job,
    /// and closing the model is the host's.
    /// </summary>
    IModelSession Session { get; }

    /// <summary>The transaction's ID, for example <c>t17</c>.</summary>
    string Transaction { get; }

    /// <summary>The session version when the lease was granted.</summary>
    long BaseVersion { get; }

    /// <summary>True when this lease joined an outer transaction instead of opening its own.</summary>
    bool IsJoined { get; }

    /// <summary>
    /// Commits the lease's changes and ends the lease.
    /// </summary>
    /// <returns>The published batch, or <c>null</c> when nothing changed or the lease joined an
    /// outer transaction (the outer commit publishes).</returns>
    Task<ModelChangeBatch?> CommitAsync(CancellationToken cancellationToken);

    /// <summary>Rolls back the lease's changes and ends the lease. Disposing an uncommitted
    /// lease does the same.</summary>
    Task RollbackAsync(CancellationToken cancellationToken);
}
