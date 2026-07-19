using System.CommandLine;
using Tomix.Cli.Commands;

namespace Tomix.Cli.Tests;

/// <summary>
/// Parse-time contract for the promoted incremental-refresh command. Pins that every option the
/// compatibility stub advertised still parses (so scripts don't break), and that typed/among
/// options reject bad values before any model is opened.
/// </summary>
public sealed class IncrementalRefreshCommandParseTests
{
    private static ParseResult Parse(params string[] args)
    {
        var root = new RootCommand("test");
        foreach (var option in GlobalOptions.All())
            root.Options.Add(option);
        var services = TestServices.Create();
        root.Subcommands.Add(new IncrementalRefreshCommand(
            [], services.State, services.Mutations, services.LoadCurrentSession).Build());
        return root.Parse(args);
    }

    [Fact]
    public void Set_AcceptsFullStubOptionSet()
    {
        var result = Parse(
            "incremental-refresh", "set", "Sales",
            "--mode", "hybrid",
            "--rolling-window-periods", "10",
            "--rolling-window-granularity", "year",
            "--incremental-periods", "3",
            "--incremental-granularity", "day",
            "--incremental-offset", "1",
            "--polling-expression", "let M = 1 in M",
            "--source-expression", "let S = 1 in S",
            "--force", "--stage", "--save", "--save-to", "out/model");

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Set_AcceptsExpressionFileOptions()
    {
        var result = Parse(
            "incremental-refresh", "set", "Sales",
            "--rolling-window-periods", "5", "--rolling-window-granularity", "month",
            "--incremental-periods", "1", "--incremental-granularity", "day",
            "--polling-expression-file", "poll.m",
            "--source-expression-file", "source.m");

        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("import")]
    [InlineData("HYBRID")]
    public void Set_ValidMode_IsCaseInsensitive(string mode)
    {
        var result = Parse("incremental-refresh", "set", "Sales", "--mode", mode);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Set_InvalidMode_FailsAtParseTime()
    {
        var result = Parse("incremental-refresh", "set", "Sales", "--mode", "te2");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Set_InvalidGranularity_FailsAtParseTime()
    {
        var result = Parse("incremental-refresh", "set", "Sales", "--rolling-window-granularity", "fortnight");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Set_NonIntegerPeriods_FailsAtParseTime()
    {
        var result = Parse("incremental-refresh", "set", "Sales", "--rolling-window-periods", "abc");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Set_InvalidSerialization_FailsAtParseTime()
    {
        var result = Parse("incremental-refresh", "set", "Sales", "--serialization", "te-folder");
        Assert.Contains(result.Errors, e => e.Message.Contains("Unknown value for --serialization"));
    }

    [Fact]
    public void Rm_AcceptsFullStubOptionSet()
    {
        var result = Parse(
            "incremental-refresh", "rm", "Sales",
            "--if-exists", "--force", "--stage", "--save", "--save-to", "out/model", "--no-sync");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Show_AcceptsTableAndModel()
    {
        var result = Parse("incremental-refresh", "show", "Sales", "./model.tmdl");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Apply_AcceptsEffectiveDateAndBootstrap()
    {
        var result = Parse(
            "incremental-refresh", "apply", "Sales",
            "--effective-date", "2024-06-01", "--no-refresh", "--max-parallelism", "4");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Apply_InvalidEffectiveDate_FailsAtParseTime()
    {
        var result = Parse("incremental-refresh", "apply", "Sales", "--effective-date", "not-a-date");
        Assert.NotEmpty(result.Errors);
    }
}
