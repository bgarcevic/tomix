using Tomix.Cli.Serve;

namespace Tomix.Cli.Tests;

/// <summary>
/// When <c>tx ui</c> stops (#369): after a grace period once the last client has left, never with
/// changes unsaved, and on Ctrl+C, which asks twice when changes are unsaved. The grace period is a
/// delay the test ends by hand.
/// </summary>
public sealed class UiLifetimeTests
{
    [Fact]
    public void BeforeAnyClientCame_NothingStops()
    {
        using var ui = new Ui();

        ui.Lifetime.ClientsChanged();

        Assert.Empty(ui.Delays);
        Assert.False(ui.Lifetime.Stopped.IsCompleted);
    }

    [Fact]
    public async Task WhenTheLastClientLeaves_ItStopsAfterTheGracePeriod()
    {
        using var ui = new Ui();
        ui.Join();
        ui.Leave();

        var delay = Assert.Single(ui.Delays);
        Assert.Equal(TimeSpan.FromSeconds(30), delay.Wait);
        Assert.False(ui.Lifetime.Stopped.IsCompleted);
        delay.Elapse();

        await ui.Lifetime.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("stopping", Assert.Single(ui.Said));
    }

    [Fact]
    public async Task AClientComingBack_CancelsTheGracePeriod()
    {
        using var ui = new Ui();
        ui.Join();
        ui.Leave();
        ui.Join();

        var delay = Assert.Single(ui.Delays);
        Assert.True(delay.Token.IsCancellationRequested);
        delay.Elapse();
        await Task.Yield();

        Assert.False(ui.Lifetime.Stopped.IsCompleted);
    }

    [Fact]
    public async Task WithChangesUnsaved_TheGracePeriodEndsWithoutStopping()
    {
        using var ui = new Ui { Unsaved = "./model" };
        ui.Join();
        ui.Leave();

        Assert.Single(ui.Delays).Elapse();
        await WaitUntil(() => ui.Said.Count > 0);

        Assert.False(ui.Lifetime.Stopped.IsCompleted);
        Assert.Contains("./model has unsaved changes", Assert.Single(ui.Said));
    }

    [Fact]
    public async Task CtrlC_StopsAtOnce_WhenEverythingIsSaved()
    {
        using var ui = new Ui();

        ui.Lifetime.Interrupt();

        await ui.Lifetime.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(ui.Said);
    }

    [Fact]
    public async Task CtrlC_AsksAgain_BeforeDiscardingUnsavedChanges()
    {
        using var ui = new Ui { Unsaved = "./model" };

        ui.Lifetime.Interrupt();
        var afterFirst = ui.Lifetime.Stopped.IsCompleted;
        ui.Lifetime.Interrupt();

        Assert.False(afterFirst);
        Assert.Contains("Press Ctrl+C again", Assert.Single(ui.Said));
        await ui.Lifetime.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    /// <summary>A lifetime over a client count the test sets, with delays it ends by hand.</summary>
    private sealed class Ui : IDisposable
    {
        private int _clients;

        public Ui()
        {
            Lifetime = new UiLifetime(
                () => _clients,
                () => Unsaved,
                TimeSpan.FromSeconds(30),
                line =>
                {
                    lock (Said)
                        Said.Add(line);
                },
                (wait, token) =>
                {
                    var delay = new Delay(wait, token);
                    Delays.Add(delay);
                    return delay.Task;
                });
        }

        public UiLifetime Lifetime { get; }

        public string? Unsaved { get; init; }

        public List<string> Said { get; } = [];

        public List<Delay> Delays { get; } = [];

        public void Join()
        {
            _clients++;
            Lifetime.ClientsChanged();
        }

        public void Leave()
        {
            _clients--;
            Lifetime.ClientsChanged();
        }

        public void Dispose() => Lifetime.Dispose();
    }

    private sealed class Delay
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Delay(TimeSpan wait, CancellationToken token)
        {
            Wait = wait;
            Token = token;
            token.Register(() => _done.TrySetCanceled(token));
        }

        public TimeSpan Wait { get; }

        public CancellationToken Token { get; }

        public Task Task => _done.Task;

        public void Elapse() => _done.TrySetResult();
    }
}
