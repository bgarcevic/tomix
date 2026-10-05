using System.Text.Json.Nodes;
using Tomix.Cli.Serve;
using Tomix.Tests.Support;

namespace Tomix.Cli.Tests;

/// <summary>
/// The registry of live sessions in <c>~/.tomix/live</c> (#369): how a second process, or a tool
/// polling <c>/status</c>, finds the session that has a model open.
/// </summary>
public sealed class LiveRegistryTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ARegisteredSession_IsFound_UntilItIsDisposed()
    {
        using var dir = new TempDir();
        var registry = new LiveRegistry(dir.Path, _ => true);
        var model = Path.Combine(dir.Path, "Sales");

        using (registry.Register(new LiveEntry(model, 42, 5100, "secret", Started)))
        {
            var found = registry.Find(model);
            Assert.Equal(new LiveEntry(model, 42, 5100, "secret", Started), found);
            Assert.Equal(new Uri("http://127.0.0.1:5100/"), found!.BaseUrl);
        }

        Assert.Null(registry.Find(model));
        Assert.Empty(Directory.EnumerateFiles(dir.Path));
    }

    [Fact]
    public void TheFile_HoldsWhatAnotherToolNeedsToConnect()
    {
        using var dir = new TempDir();
        var registry = new LiveRegistry(dir.Path, _ => true);
        var model = Path.Combine(dir.Path, "Sales Model");

        using var entry = registry.Register(new LiveEntry(model, 42, 5100, "secret", Started));
        var file = Assert.Single(Directory.EnumerateFiles(dir.Path));
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();

        Assert.StartsWith("sales_model-", Path.GetFileName(file));
        Assert.Equal(["model", "pid", "port", "url", "token", "startedAt"], json.Select(pair => pair.Key));
        Assert.Equal("http://127.0.0.1:5100/", (string?)json["url"]);
        Assert.Equal("2026-10-05T12:00:00Z", (string?)json["startedAt"]);
    }

    [Fact]
    public void AnEntryWhoseProcessHasExited_IsStale_AndRemoved()
    {
        using var dir = new TempDir();
        var model = Path.Combine(dir.Path, "Sales");
        _ = new LiveRegistry(dir.Path, _ => true).Register(new LiveEntry(model, 42, 5100, "secret", Started));
        var registry = new LiveRegistry(dir.Path, _ => false);

        Assert.Null(registry.Find(model));
        Assert.Empty(registry.All());
        Assert.Empty(Directory.EnumerateFiles(dir.Path));
    }

    [Fact]
    public void Disposing_LeavesAnotherProcessesEntryForTheSameModel()
    {
        using var dir = new TempDir();
        var registry = new LiveRegistry(dir.Path, _ => true);
        var model = Path.Combine(dir.Path, "Sales");
        var first = registry.Register(new LiveEntry(model, 42, 5100, "old", Started));
        using var second = registry.Register(new LiveEntry(model, 43, 5200, "new", Started));

        first.Dispose();

        Assert.Equal(43, registry.Find(model)!.ProcessId);
    }

    [Fact]
    public void TheSameFolder_SpelledDifferently_IsOneSession()
    {
        using var dir = new TempDir();
        var registry = new LiveRegistry(dir.Path, _ => true);
        var model = Path.Combine(dir.Path, "Sales");
        using var entry = registry.Register(new LiveEntry(model, 42, 5100, "secret", Started));

        Assert.NotNull(registry.Find(model + Path.DirectorySeparatorChar));
        Assert.NotNull(registry.Find(Path.Combine(dir.Path, "x", "..", "Sales")));
        if (OperatingSystem.IsWindows())
            Assert.NotNull(registry.Find(model.ToUpperInvariant()));
        Assert.Null(registry.Find(Path.Combine(dir.Path, "Other")));
    }

    [Fact]
    public void AnUnreadableFile_IsIgnored()
    {
        using var dir = new TempDir();
        var registry = new LiveRegistry(dir.Path, _ => true);
        var model = Path.Combine(dir.Path, "Sales");
        File.WriteAllText(registry.PathFor(model), "{ not json");

        Assert.Null(registry.Find(model));
        Assert.Empty(registry.All());
    }
}
