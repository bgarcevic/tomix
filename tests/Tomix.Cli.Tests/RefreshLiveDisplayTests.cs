using Spectre.Console;
using Spectre.Console.Testing;
using Tomix.App.Refresh;
using Tomix.Cli.Output;
using Tomix.Core.Models;
using Tomix.Core.Results;
using TableView = Tomix.Cli.Output.RefreshLiveDisplay.TableView;

namespace Tomix.Cli.Tests;

/// <summary>
/// The refresh live panel. Names come straight from the model and every line is parsed as Spectre
/// markup, so a table named <c>Sales [EUR]</c> must be escaped — unescaped it threw on every
/// update and silently froze the display (#257). The panel also has to stay readable on models
/// with many tables in flight: a single alphabetical status line got cut off at the terminal width
/// and showed the same waiting tables for the whole refresh.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class RefreshLiveDisplayTests
{
    private static readonly TimeSpan Now = TimeSpan.FromSeconds(75);

    private static TableView Active(string name, string phase, int startedSecondsAgo, long rows = 0, string? partition = null)
        => new(name, partition, phase, rows, Completed: false, Now - TimeSpan.FromSeconds(startedSecondsAgo));

    private static TableView Done(string name, long rows)
        => new(name, null, "done", rows, Completed: true, TimeSpan.Zero);

    private static IReadOnlyList<string> Lines(IReadOnlyList<TableView> tables, string? modelPhase = null, int maxListed = 6)
        => RefreshLiveDisplay.BuildLines("Refreshing model (full)...", Now, tables, modelPhase, maxListed);

    private static string Plain(string markup) => Markup.Remove(markup);

    [Fact]
    public void Header_ShowsLabelAndElapsedTime()
        => Assert.EndsWith("Refreshing model (full) · 01:15", Plain(Lines([])[0]));

    [Fact]
    public void ListsInProgressTables_OldestFirst_WithStepRowsAndRunningTime()
    {
        var lines = Lines(
        [
            Active("Orders", "query", startedSecondsAgo: 3),
            Active("Sales", "read", startedSecondsAgo: 48, rows: 12345, partition: "2024Q1"),
            Done("Date", 14),
        ]).Select(Plain).ToList();

        Assert.Equal("  1 table done · 2 in progress · 12,359 rows", lines[1]);
        Assert.Matches(@"^  Reading\s+Sales › 2024Q1\s+12,345 rows  00:48$", lines[2]);
        Assert.Matches(@"^  Querying\s+Orders\s+00:03$", lines[3]);
        Assert.Equal(4, lines.Count);
    }

    [Fact]
    public void CapsTheList_AndCountsTheRest()
    {
        var tables = Enumerable.Range(1, 9).Select(i => Active($"T{i}", "query", startedSecondsAgo: 100 - i)).ToList();

        var lines = Lines(tables, maxListed: 3).Select(Plain).ToList();

        Assert.Contains("T1", lines[2]);
        Assert.Contains("T3", lines[4]);
        Assert.Equal("  + 6 more in progress", lines[5]);
        Assert.Equal(6, lines.Count);
    }

    [Fact]
    public void ShowsTheModelStep_OnceNoTableIsInProgress()
    {
        var lines = Lines([Done("Sales", 8)], modelPhase: "building relationships").Select(Plain).ToList();

        Assert.Equal("  Building relationships...", lines[^1]);
    }

    [Fact]
    public void BeforeAnyTable_SaysItIsWaiting()
        => Assert.Equal("  Waiting for the server...", Plain(Lines([])[1]));

    [Theory]
    [InlineData("Sales [EUR]")]
    [InlineData("T[a]b")]
    [InlineData("Rate: 5%")]
    public void EveryLine_IsValidMarkup_WithMarkupCharactersInNames(string name)
    {
        var lines = Lines([Active(name, "read", 5, rows: 10, partition: $"{name} P1"), Done($"{name} done", 1)]);

        foreach (var line in lines)
            _ = new Markup(line);
        Assert.Contains(lines, l => Plain(l).Contains(name, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_ReturnsTheResult_WhileEventsWithMarkupNamesArrive()
    {
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = new TestConsole();
        try
        {
            var display = new RefreshLiveDisplay();
            var result = await display.RunAsync("Refreshing model...", async () =>
            {
                display.Progress.Report(new RefreshProgress("Sales [EUR]", 100, "query", Completed: false));
                await Task.Delay(300);   // let the render loop draw a frame with the names
                display.Progress.Report(new RefreshProgress("Sales [EUR]", 250, "read", Completed: false, Partition: "P[1]"));
                display.Progress.Report(new RefreshProgress(null, null, "building relationships", Completed: false));
                await Task.Delay(300);
                return TomixResult<RefreshModelResult>.Ok(Result());
            });

            Assert.True(result.Success);
            Assert.NotNull(result.Data);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    private static RefreshModelResult Result() => new(
        Server: "powerbi://api.powerbi.com/v1.0/myorg/W",
        Database: "Model",
        RefreshType: "full",
        DurationMs: 1,
        Tables: [],
        Totals: null,
        Script: null);
}
