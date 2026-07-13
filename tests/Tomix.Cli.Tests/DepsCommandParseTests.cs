using System.CommandLine;
using Tomix.Cli.Commands;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

public sealed class DepsCommandParseTests
{
    private static ParseResult Parse(params string[] args)
    {
        var root = new RootCommand("test");
        foreach (var option in GlobalOptions.All())
            root.Options.Add(option);
        IReadOnlyList<IModelProvider> providers = [];
        root.Subcommands.Add(new DepsCommand(providers).Build());
        return root.Parse(args);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    public void Deps_MaxDepthBelowOne_FailsAtParseTime(string value)
    {
        var result = Parse("deps", "Sales/Total", "--max-depth", value);

        Assert.Contains(result.Errors, e => e.Message.Contains("--max-depth must be at least 1"));
    }

    [Fact]
    public void Deps_PositiveMaxDepth_Parses()
        => Assert.Empty(Parse("deps", "Sales/Total", "--max-depth", "1").Errors);
}
