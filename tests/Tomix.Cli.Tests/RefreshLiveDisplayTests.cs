using Spectre.Console;
using Spectre.Console.Testing;
using Tomix.App.Refresh;
using Tomix.Cli.Output;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.Cli.Tests;

/// <summary>
/// The refresh live status line is composed from raw model names and assigned to Spectre's
/// <see cref="StatusContext.Status"/>, which parses the string as markup — so a table named
/// <c>Sales [EUR]</c> used to throw on every status update and, the throw being swallowed by
/// the trace sink's catch-all, silently freeze the display (#257). These tests pin the
/// escaping and prove the live status keeps updating with markup characters in names.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class RefreshLiveDisplayTests
{
    [Theory]
    [InlineData("Sales [EUR]", "Sales [[EUR]]")]
    [InlineData("T[a]b", "T[[a]]b")]
    [InlineData("Rate: 5%", "Rate: 5%")]
    public void BuildStatus_EscapesObjectNames(string table, string escapedTable)
    {
        var status = RefreshLiveDisplay.BuildStatus([(table, 0L, "query")]);

        Assert.Equal($"{escapedTable} query", status);
        // Parses the composed status as Spectre markup — exactly what the
        // StatusContext.Status setter does — so this throws MarkupParseException
        // if any part is under- or over-escaped.
        _ = new Markup(status);
    }

    [Fact]
    public void BuildStatus_JoinsActiveTables()
    {
        var status = RefreshLiveDisplay.BuildStatus(
        [
            ("Sales [EUR]", 0L, "query"),
            ("T[a]b", 0L, "read"),
        ]);

        Assert.Equal("Sales [[EUR]] query  |  T[[a]]b read", status);
    }

    [Fact]
    public async Task BuildStatus_ShowsRowCountsAndFallsBackToProcessing()
    {
        var status = RefreshLiveDisplay.BuildStatus([("Sales [EUR]", 1234L, null)]);

        Assert.Contains("Sales [[EUR]]", status);
        Assert.Contains("rows", status);
        Assert.Contains("processing", status);
    }

    [Fact]
    public async Task LiveStatus_KeepsUpdating_WhenTableNamesContainMarkup()
    {
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = new TestConsole();
        try
        {
            var display = new RefreshLiveDisplay();
            var result = await display.RunAsync("Refreshing model...", () =>
            {
                // Before the fix the first report threw MarkupParseException out of the
                // StatusContext.Status setter; called directly here it surfaces instead of
                // being swallowed by the trace sink's catch-all as it is in production.
                display.Progress.Report(new RefreshProgress("Sales [EUR]", 100, "query", Completed: false));
                display.Progress.Report(new RefreshProgress("Sales [EUR]", 250, "read", Completed: false));
                display.Progress.Report(new RefreshProgress("T[a]b", 10, "query", Completed: false));
                return Task.FromResult(TomixResult<RefreshModelResult>.Ok(Result()));
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
