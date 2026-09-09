using System.CommandLine;
using Tomix.Cli.Commands;

namespace Tomix.Cli.Tests;

/// <summary>
/// The shared --type vocabulary and the full-name command aliases: ls takes -t like every other
/// kind-disambiguating command, find/replace accept --type for kind scoping, `tx list` and
/// `tx remove` route to ls/rm, and an unknown --type value fails through the shared
/// TOMIX_INVALID_TYPE error before any model is opened.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class TypeVocabularyTests
{
    // ── Command aliases ─────────────────────────────────────────────────────

    [Fact]
    public void ListAlias_ParsesLikeLs()
    {
        var root = BuildRoot();

        Assert.Empty(root.Parse(["ls"]).Errors);
        Assert.Empty(root.Parse(["list"]).Errors);
    }

    [Fact]
    public void RemoveAlias_ParsesLikeRm()
    {
        var root = BuildRoot();

        Assert.Empty(root.Parse(["rm", "Sales/X"]).Errors);
        Assert.Empty(root.Parse(["remove", "Sales/X"]).Errors);
    }

    [Fact]
    public void MisspelledCommandName_Fails()
    {
        // Negative control for the alias tests: without it, empty-errors prove nothing.
        var parsed = BuildRoot().Parse(["lst"]);

        Assert.NotEmpty(parsed.Errors);
    }

    // ── ls/find/replace --type ──────────────────────────────────────────────

    [Theory]
    [InlineData("ls", "--type", "calculatedcolumn")]
    [InlineData("ls", "-t", "Table")]
    [InlineData("find", "pattern", "--type", "measure")]
    [InlineData("find", "pattern", "-t", "measure")]
    [InlineData("replace", "a", "b", "--type", "column")]
    [InlineData("replace", "a", "b", "-t", "column")]
    public void TypeOptionAndAlias_Parse(params string[] args)
    {
        var parsed = BuildRoot().Parse(args);
        Assert.Empty(parsed.Errors);
    }

    [Fact]
    public void InvalidTypeValue_FailsWithSharedError_BeforeOpeningAModel()
    {
        var services = TestServices.Create();
        var root = TestRoot.With(new FindCommand([], services.State).Build());

        var captured = ConsoleCapture.Invoke(root.Parse(["find", "pattern", "--type", "bogus"]));

        Assert.Equal(2, captured.ExitCode);
        Assert.Equal("", captured.Stdout);
        Assert.Contains("calculatedcolumn", captured.Stderr);
    }

    private static RootCommand BuildRoot()
    {
        var services = TestServices.Create();
        return TestRoot.With(
            new LsCommand([], services.State).Build(),
            new RmCommand([], services.State, services.Mutations).Build(),
            new FindCommand([], services.State).Build(),
            new ReplaceCommand([], services.State, services.Mutations).Build());
    }
}
