using System.CommandLine;
using Tomix.Cli.Commands;

namespace Tomix.Cli.Tests;

/// <summary>
/// Parse-time --serialization validation on the mutation commands beyond add (ported from the
/// add audit fixes): bad values fail before any model is opened, valid values are case-insensitive.
/// </summary>
public sealed class MutationCommandParseTests
{
    private static ParseResult Parse(Command command, params string[] args)
    {
        var root = new RootCommand("test");
        foreach (var option in GlobalOptions.All())
            root.Options.Add(option);
        root.Subcommands.Add(command);
        return root.Parse(args);
    }

    [Fact]
    public void Rm_InvalidSerialization_FailsAtParseTime()
    {
        var result = Parse(new RmCommand([]).Build(), "rm", "Sales/M", "--serialization", "te-folder");

        Assert.Contains(result.Errors, e => e.Message.Contains("Unknown value for --serialization"));
    }

    [Theory]
    [InlineData("TMDL")]
    [InlineData("bim")]
    [InlineData("tmsl")]
    public void Rm_ValidSerialization_IsCaseInsensitive(string value)
    {
        var result = Parse(new RmCommand([]).Build(), "rm", "Sales/M", "--serialization", value);

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Init_AcceptsPbipButNotTmsl()
    {
        var init = new InitCommand();

        Assert.Empty(Parse(init.Build(), "init", "out/model", "--serialization", "pbip").Errors);

        var bad = Parse(new InitCommand().Build(), "init", "out/model", "--serialization", "tmsl");
        Assert.Contains(bad.Errors, e => e.Message.Contains("Unknown value for --serialization"));
    }
}
