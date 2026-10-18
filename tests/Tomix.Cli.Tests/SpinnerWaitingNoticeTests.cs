using Tomix.Cli.Output;

namespace Tomix.Cli.Tests;

/// <summary>
/// A network wait must not leave the user staring at a silent spinner: after a while the label
/// says what tx is waiting on and how to stop.
/// </summary>
public sealed class SpinnerWaitingNoticeTests
{
    [Fact]
    public async Task SlowWork_RaisesTheNotice_ThenStillReturnsTheResult()
    {
        var work = new TaskCompletionSource<int>();
        var noticed = new TaskCompletionSource();

        var running = CliSpinner.WithWaitingNotice(work.Task, TimeSpan.Zero, noticed.SetResult);
        await noticed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        work.SetResult(42);

        Assert.Equal(42, await running);
    }

    [Fact]
    public async Task FastWork_NeverRaisesTheNotice()
    {
        var noticed = false;

        var result = await CliSpinner.WithWaitingNotice(Task.FromResult(7), TimeSpan.FromHours(1), () => noticed = true);

        Assert.Equal(7, result);
        Assert.False(noticed);
    }

    [Fact]
    public async Task FailingWork_PropagatesItsException()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CliSpinner.WithWaitingNotice(Task.FromException<int>(new InvalidOperationException()), TimeSpan.FromHours(1), () => { }));
    }

    [Fact]
    public void WaitingMessage_NamesTheHostAndHowToCancel()
    {
        Assert.Equal(
            "Still waiting for login.microsoftonline.com... (Ctrl+C to cancel)",
            CliSpinner.WaitingMessage("login.microsoftonline.com"));
    }

    [Theory]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/Sales", "api.powerbi.com")]
    [InlineData("asazure://westeurope.asazure.windows.net/srv", "westeurope.asazure.windows.net")]
    [InlineData("https://login.microsoftonline.com/common", "login.microsoftonline.com")]
    [InlineData("localhost:51234", null)]
    [InlineData("model.bim", null)]
    [InlineData(null, null)]
    public void WaitTarget_IsTheRemoteHost(string? server, string? expected)
    {
        Assert.Equal(expected, CliSpinner.WaitTarget(server));
    }
}
