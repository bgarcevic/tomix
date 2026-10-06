using System.CommandLine;
using System.Text.Json.Nodes;
using Tomix.Cli.Commands;
using Tomix.Cli.Serve;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;
using Tomix.Provider.Tom;
using Tomix.Tests.Support;

namespace Tomix.Cli.Tests;

/// <summary>
/// <c>tx ui</c> on a model another <c>tx ui</c> already holds (#369): it hands out the running
/// session's URL instead of opening a second session. Serving itself is covered by
/// <see cref="SharedSessionTests"/>, which needs no network.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class UiCommandTests
{
    private static readonly IReadOnlyList<IModelProvider> Providers = [new TmdlModelProvider(), new TomFileModelProvider()];

    [Fact]
    public void AModelAlreadyOpen_PrintsItsUrl_AndOpensItWhenAsked()
    {
        using var model = SampleModel.CopyToTemp();
        using var ui = new UiRun();
        using var _ = ui.Registry.Register(new LiveEntry(Path.GetFullPath(model.Path), Environment.ProcessId, 51873, "secret", DateTimeOffset.UtcNow));

        var run = ui.Invoke("ui", model.Path, "--open");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("http://127.0.0.1:51873/?token=secret", run.Stdout.Trim());
        Assert.Contains("already open in tx ui", run.Stderr);
        Assert.Equal(["http://127.0.0.1:51873/?token=secret"], ui.Opened);
    }

    [Fact]
    public void AModelAlreadyOpen_InJson_SaysItJoined()
    {
        using var model = SampleModel.CopyToTemp();
        using var ui = new UiRun();
        using var _ = ui.Registry.Register(new LiveEntry(Path.GetFullPath(model.Path), Environment.ProcessId, 51873, "secret", DateTimeOffset.UtcNow));

        var run = ui.Invoke("ui", model.Path, "--output-format", "json");

        var data = JsonNode.Parse(run.Stdout)!["data"]!;
        Assert.Equal(0, run.ExitCode);
        Assert.True((bool)data["joined"]!);
        Assert.Equal(51873, (int)data["port"]!);
        Assert.Equal(Environment.ProcessId, (int)data["processId"]!);
        Assert.Empty(ui.Opened);
    }

    [Theory]
    [InlineData("--port", "70000")]
    [InlineData("--grace", "-1")]
    public void OutOfRangeOptions_AreUsageErrors(string option, string value)
    {
        using var ui = new UiRun();

        var run = ui.Invoke("ui", "./model", option, value);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains(option, run.Stderr);
    }

    /// <summary>A root with <c>tx ui</c> alone, over a registry in a temporary folder and a browser that only records.</summary>
    private sealed class UiRun : IDisposable
    {
        private readonly TempDir _live = new();

        public UiRun()
        {
            Registry = new LiveRegistry(_live.Path);
        }

        public LiveRegistry Registry { get; }

        public List<string> Opened { get; } = [];

        public ConsoleCapture.Captured Invoke(params string[] args)
        {
            var services = TestServices.Create();
            var root = new RootCommand();
            foreach (var option in GlobalOptions.All())
                root.Options.Add(option);
            root.Subcommands.Add(new UiCommand(
                Providers,
                services.State,
                services.Staging,
                TestRoot.Version,
                (_, _) => new RootCommand(),
                Registry,
                url =>
                {
                    Opened.Add(url);
                    return true;
                }).Build());
            var parsed = root.Parse(args);
            return parsed.Errors.Count > 0
                ? ConsoleCapture.Run(() => Program.Run(args))
                : ConsoleCapture.InvokeThroughProgram(parsed);
        }

        public void Dispose() => _live.Dispose();
    }
}
