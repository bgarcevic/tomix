using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Testing;
using Tomix.App.Refresh;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

/// <summary>
/// The refresh summary's per-partition rows and model phases: the text table, and the JSON
/// fields (<c>processMs</c>, <c>partitions</c>, <c>phases</c>), which are additive — a result
/// without trace detail must serialize exactly as before.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class RefreshResultOutputTests
{
    private static RefreshModelResult Detailed() => new(
        "powerbi://api.powerbi.com/v1.0/myorg/ws", "Prod", "full", DurationMs: 2000,
        Tables:
        [
            new RefreshTableResult("Events", 4, 40, 60, 100, ProcessMs: 15,
                Partitions: [new("2026Q308", 2, 10, 20, 30), new("2026Q309", 2, 30, 40, 70)]),
            new RefreshTableResult("Customer", 5, 16, 0, 16,
                Partitions: [new("Customer", 5, 16, 0, 16)]),
        ],
        Totals: new RefreshTableResult("Total", 9, 56, 60, 116, ProcessMs: 15),
        Script: null,
        Phases: [new("load", 3, 205), new("relationships", 3, 4), new("commit", 2, 219)]);

    [Fact]
    public void Text_ListsPartitionsOfMultiPartitionTables_AndThePhases()
    {
        var output = Render(Detailed());

        Assert.Contains("└ 2026Q308", output);
        Assert.Contains("└ 2026Q309", output);
        // A single-partition table's partition row would only repeat the table row.
        Assert.DoesNotContain("└ Customer", output);
        Assert.Contains("Process", output);
        Assert.Contains("Data load", output);
        Assert.Contains("Relationships", output);
        Assert.Contains("Commit", output);
    }

    [Fact]
    public void Json_CarriesPartitionsProcessAndPhases()
    {
        var root = JsonDocument.Parse(JsonOutput.Serialize(Detailed())).RootElement;

        var events = root.GetProperty("tables")[0];
        Assert.Equal(15, events.GetProperty("processMs").GetInt64());
        var partition = events.GetProperty("partitions")[1];
        Assert.Equal("2026Q309", partition.GetProperty("partition").GetString());
        Assert.Equal(2, partition.GetProperty("rows").GetInt64());
        Assert.Equal(70, partition.GetProperty("totalMs").GetInt64());

        var phase = root.GetProperty("phases")[2];
        Assert.Equal("commit", phase.GetProperty("phase").GetString());
        Assert.Equal(2, phase.GetProperty("count").GetInt32());
        Assert.Equal(219, phase.GetProperty("durationMs").GetInt64());
    }

    [Fact]
    public void Json_OmitsTraceDetail_WhenNoneWasCaptured()
    {
        var result = new RefreshModelResult(
            "powerbi://api.powerbi.com/v1.0/myorg/ws", "Prod", "full", DurationMs: 1000,
            Tables: [new RefreshTableResult("Sales", 0, 0, 0, 0)],
            Totals: new RefreshTableResult("Total", 0, 0, 0, 0),
            Script: null);

        var root = JsonDocument.Parse(JsonOutput.Serialize(result)).RootElement;

        Assert.False(root.TryGetProperty("phases", out _));
        Assert.False(root.GetProperty("tables")[0].TryGetProperty("partitions", out _));
    }

    private static string Render(RefreshModelResult result)
    {
        var original = AnsiConsole.Console;
        var console = new TestConsole();
        console.Profile.Width = 200;
        AnsiConsole.Console = console;
        try
        {
            RefreshRenderer.Render(result);
            return console.Output;
        }
        finally
        {
            AnsiConsole.Console = original;
        }
    }
}
