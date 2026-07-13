using System.CommandLine;
using Tomix.Cli.Commands;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

/// <summary>
/// Guards against option-lookalike tokens being silently bound to optional positional arguments
/// (a typo'd flag must fail loudly instead of becoming a path filter or model path).
/// </summary>
public sealed class UnknownOptionGuardTests
{
    private static (ParseResult Result, string[] Args) Parse(params string[] args)
    {
        var root = new RootCommand("test");
        foreach (var option in GlobalOptions.All())
            root.Options.Add(option);
        IReadOnlyList<IModelProvider> providers = [];
        root.Subcommands.Add(new LsCommand(providers).Build());
        root.Subcommands.Add(new GetCommand(providers).Build());
        root.Subcommands.Add(new FindCommand(providers).Build());
        return (root.Parse(args), args);
    }

    [Fact]
    public void Ls_TypodFlag_IsDetected()
    {
        var (result, args) = Parse("ls", "--bogusflag");

        Assert.Empty(result.Errors);
        Assert.Equal("--bogusflag", UnknownOptionGuard.FindOffendingToken(result, args));
    }

    [Fact]
    public void Get_TypodFlagAfterPath_IsDetected()
    {
        var (result, args) = Parse("get", "Budget", "--bogusflag");

        Assert.Equal("--bogusflag", UnknownOptionGuard.FindOffendingToken(result, args));
    }

    [Fact]
    public void Ls_KnownOptionsAndPathFilter_PassClean()
    {
        var (result, args) = Parse("ls", "Sa*", "--type", "table", "--paths-only");

        Assert.Empty(result.Errors);
        Assert.Null(UnknownOptionGuard.FindOffendingToken(result, args));
    }

    [Fact]
    public void Find_DashPatternAfterSeparator_IsAllowed()
    {
        var (result, args) = Parse("find", "--", "-[Measure]");

        Assert.Empty(result.Errors);
        Assert.Null(UnknownOptionGuard.FindOffendingToken(result, args));
    }

    [Fact]
    public void Find_DashPatternWithoutSeparator_IsDetected()
    {
        var (result, args) = Parse("find", "-pattern");

        Assert.Equal("-pattern", UnknownOptionGuard.FindOffendingToken(result, args));
    }
}
