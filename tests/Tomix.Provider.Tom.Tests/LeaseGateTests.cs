namespace Tomix.Provider.Tom.Tests;

/// <summary>The order in which <see cref="LeaseGate"/> grants waiters.</summary>
public sealed class LeaseGateTests
{
    [Fact]
    public async Task AnIdleWaiter_GoesAfterRequestsThatAskLater()
    {
        var gate = new LeaseGate();
        await gate.WaitAsync(CancellationToken.None);

        var idle = gate.WaitIdleAsync();
        var request = gate.WaitAsync(CancellationToken.None);
        gate.Release();

        await request.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(idle.IsCompleted);

        gate.Release();
        await idle.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
