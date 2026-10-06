namespace Tomix.Cli.Serve;

/// <summary>
/// When <c>tx ui</c> stops (#369): a grace period after the last client leaves, and Ctrl+C. Neither
/// discards unsaved changes on its own: with changes unsaved, the grace period ends without
/// stopping, and Ctrl+C asks to be pressed again.
/// </summary>
internal sealed class UiLifetime : IDisposable
{
    private readonly Lock _sync = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<int> _clientCount;
    private readonly Func<string?> _unsavedModel;
    private readonly TimeSpan _grace;
    private readonly Action<string> _say;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource? _countdown;
    private bool _hadClients;
    private bool _warned;

    /// <param name="clientCount">How many clients are connected now.</param>
    /// <param name="unsavedModel">The open model when it has unsaved changes, otherwise <c>null</c>.</param>
    /// <param name="grace">How long to keep running once the last client has left.</param>
    /// <param name="say">Tells the person at the terminal what happens.</param>
    /// <param name="delay">Waits out the grace period; replaced in tests.</param>
    public UiLifetime(
        Func<int> clientCount,
        Func<string?> unsavedModel,
        TimeSpan grace,
        Action<string> say,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _clientCount = clientCount;
        _unsavedModel = unsavedModel;
        _grace = grace;
        _say = say;
        _delay = delay ?? ((wait, cancellationToken) => Task.Delay(wait, cancellationToken));
    }

    /// <summary>Completes when <c>tx ui</c> should stop.</summary>
    public Task Stopped => _stopped.Task;

    /// <summary>
    /// Call after a client connects or leaves. The grace period starts when the last one leaves,
    /// and not before a first one has come, so the URL can be opened at any time.
    /// </summary>
    public void ClientsChanged()
    {
        lock (_sync)
        {
            if (_clientCount() > 0)
            {
                _hadClients = true;
                _countdown?.Cancel();
                _countdown = null;
                return;
            }

            if (!_hadClients || _countdown is not null)
                return;
            _countdown = new CancellationTokenSource();
            _ = CountDownAsync(_countdown);
        }
    }

    /// <summary>Ctrl+C: stops, unless changes are unsaved and this is the first press.</summary>
    public void Interrupt()
    {
        lock (_sync)
        {
            if (!_warned && _unsavedModel() is { } model)
            {
                _warned = true;
                _say($"{model} has unsaved changes. Press Ctrl+C again to discard them and stop.");
                return;
            }
        }

        _stopped.TrySetResult();
    }

    /// <summary>Stops at once, for a termination signal.</summary>
    public void Stop() => _stopped.TrySetResult();

    /// <summary>Ends a grace period still running.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            _countdown?.Cancel();
            _countdown = null;
        }
    }

    private async Task CountDownAsync(CancellationTokenSource countdown)
    {
        try
        {
            await _delay(_grace, countdown.Token);
        }
        catch (OperationCanceledException)
        {
            countdown.Dispose();
            return;
        }

        lock (_sync)
        {
            if (!ReferenceEquals(_countdown, countdown))
                return;
            _countdown = null;
        }

        countdown.Dispose();
        if (_unsavedModel() is { } model)
        {
            _say($"No clients are connected, but {model} has unsaved changes, so tx ui keeps running. Open the page to save them, or press Ctrl+C twice to discard them.");
            return;
        }

        _say("No clients are connected: stopping.");
        _stopped.TrySetResult();
    }
}
