namespace Tomix.Provider.Tom;

/// <summary>
/// The FIFO async lock behind a live session's leases (ADR 0002 §1). Waiters are granted in the
/// order they asked; a waiter cancelled before it is granted leaves the queue and never holds
/// the gate. Not reentrant: the session handles reentrancy by joining, before it gets here.
/// </summary>
internal sealed class LeaseGate
{
    private readonly object _lock = new();
    private readonly LinkedList<TaskCompletionSource> _waiters = new();
    private readonly Queue<TaskCompletionSource> _idle = new();
    private bool _held;

    /// <summary>
    /// Waits for the gate behind every waiter of <see cref="WaitAsync"/>, including those that
    /// ask later: background work that must not delay a request (#423). Not cancellable.
    /// </summary>
    public Task WaitIdleAsync()
    {
        lock (_lock)
        {
            if (!_held)
            {
                _held = true;
                return Task.CompletedTask;
            }

            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _idle.Enqueue(waiter);
            return waiter.Task;
        }
    }

    public Task WaitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (!_held)
            {
                _held = true;
                return Task.CompletedTask;
            }

            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var node = _waiters.AddLast(waiter);
            if (cancellationToken.CanBeCanceled)
            {
                var registration = cancellationToken.Register(() =>
                {
                    lock (_lock)
                    {
                        // Granted already: the waiter owns the gate and must release it.
                        if (node.List is null)
                            return;
                        _waiters.Remove(node);
                    }

                    waiter.TrySetCanceled(cancellationToken);
                });
                waiter.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            return waiter.Task;
        }
    }

    /// <summary>Hands the gate to the next waiter, or frees it when nobody waits.</summary>
    public void Release()
    {
        lock (_lock)
        {
            if (!_held)
                throw new InvalidOperationException("The lease gate is not held.");

            if (_waiters.First is { } next)
            {
                _waiters.RemoveFirst();
                next.Value.SetResult();
            }
            else if (_idle.TryDequeue(out var idle))
            {
                idle.SetResult();
            }
            else
            {
                _held = false;
            }
        }
    }
}
