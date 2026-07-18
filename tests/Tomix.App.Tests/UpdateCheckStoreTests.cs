using Tomix.App.Update;

namespace Tomix.App.Tests;

public sealed class UpdateCheckStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tomix-update-check-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Load_ReturnsNullWhenFileMissing()
    {
        var store = new UpdateCheckStore(_dir);

        Assert.Null(store.Load());
        Assert.True(store.IsStale(TimeSpan.FromHours(24)));
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var clock = new FakeClock();
        var store = new UpdateCheckStore(_dir, clock);

        store.Save("0.3.0");
        var state = store.Load();

        Assert.NotNull(state);
        Assert.Equal("0.3.0", state.LatestVersion);
        Assert.Equal(clock.Now, state.LastCheckedUtc);
    }

    [Fact]
    public void IsStale_HonorsTtlAgainstTheInjectedClock()
    {
        var clock = new FakeClock();
        var store = new UpdateCheckStore(_dir, clock);
        store.Save("0.3.0");

        Assert.False(store.IsStale(TimeSpan.FromHours(24)));

        clock.Now += TimeSpan.FromHours(23);
        Assert.False(store.IsStale(TimeSpan.FromHours(24)));

        clock.Now += TimeSpan.FromHours(2);
        Assert.True(store.IsStale(TimeSpan.FromHours(24)));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ \"lastCheckedUtc\": \"2026-07-18T12:00:00+00:00\" }")]
    [InlineData("")]
    public void Load_SelfHealsOnCorruptOrIncompleteFile(string contents)
    {
        var store = new UpdateCheckStore(_dir);
        File.WriteAllText(store.FilePath, contents);

        Assert.Null(store.Load());
        Assert.True(store.IsStale(TimeSpan.FromHours(24)));

        store.Save("0.4.0");
        Assert.Equal("0.4.0", store.Load()?.LatestVersion);
    }
}
