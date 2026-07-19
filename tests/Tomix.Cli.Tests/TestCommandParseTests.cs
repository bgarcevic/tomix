using System.CommandLine;
using Tomix.Cli.Commands;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

/// <summary>
/// Parse-time validation for <c>tx test</c>: the path argument defaults to the current
/// directory, options bind, and bad <c>--max-rows</c> values fail before any connection opens.
/// </summary>
public sealed class TestCommandParseTests
{
    private static ParseResult Parse(params string[] args)
    {
        var root = new RootCommand("test");
        foreach (var option in GlobalOptions.All())
            root.Options.Add(option);
        IReadOnlyList<IModelProvider> providers = [];
        root.Subcommands.Add(new TestCommand(providers, TestServices.Create()).Build());
        return root.Parse(args);
    }

    [Fact]
    public void Test_PathDefaultsToCurrentDirectory()
    {
        var result = Parse("test");

        Assert.Empty(result.Errors);
        Assert.Equal(".", result.GetValue<string>("path"));
    }

    [Fact]
    public void Test_AllOptionsBind()
    {
        var result = Parse(
            "test", "./tests",
            "--update",
            "--filter", "totals/*",
            "--param", "a=1", "--param", "b=2",
            "--max-rows", "500",
            "--trx", "results.trx",
            "--ci", "vsts");

        Assert.Empty(result.Errors);
        Assert.Equal("./tests", result.GetValue<string>("path"));
        Assert.True(result.GetValue<bool>("--update"));
        Assert.Equal("totals/*", result.GetValue<string?>("--filter"));
        Assert.Equal(["a=1", "b=2"], result.GetValue<string[]>("--param")!);
        Assert.Equal(500, result.GetValue<int?>("--max-rows"));
        Assert.Equal("results.trx", result.GetValue<string?>("--trx"));
        Assert.Equal("vsts", result.GetValue<string?>("--ci"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void Test_MaxRowsBelowOne_FailsAtParseTime(string maxRows)
    {
        var result = Parse("test", "--max-rows", maxRows);

        Assert.Contains(result.Errors, e => e.Message.Contains("--max-rows must be at least 1"));
    }
}
