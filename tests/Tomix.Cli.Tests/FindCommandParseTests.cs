using System.CommandLine;
using Tomix.Cli.Commands;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

/// <summary>
/// Parse-time --in scope validation: bad values fail before any model is opened instead of
/// silently matching nothing, valid values are case-insensitive.
/// </summary>
public sealed class FindCommandParseTests
{
    private static ParseResult Parse(params string[] args)
    {
        var root = new RootCommand("test");
        foreach (var option in GlobalOptions.All())
            root.Options.Add(option);
        IReadOnlyList<IModelProvider> providers = [];
        var services = TestServices.Create();
        root.Subcommands.Add(new FindCommand(providers, services.State).Build());
        return root.Parse(args);
    }

    [Fact]
    public void Find_InvalidScope_FailsAtParseTime()
    {
        var result = Parse("find", "pattern", "--in", "bogus");

        Assert.Contains(result.Errors, e => e.Message.Contains("Unknown value for --in"));
    }

    [Theory]
    [InlineData("all")]
    [InlineData("names")]
    [InlineData("EXPRESSIONS")]
    [InlineData("formatstrings")]
    [InlineData("displayFolders")]
    [InlineData("annotations")]
    public void Find_ValidScope_IsCaseInsensitive(string scope)
    {
        var result = Parse("find", "pattern", "--in", scope);

        Assert.Empty(result.Errors);
    }
}
