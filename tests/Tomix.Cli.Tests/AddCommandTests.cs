using System.CommandLine;
using Tomix.Cli.Commands;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

/// <summary>
/// Parse-level tests for <c>tx add</c>: <c>-q</c> as the global quiet flag, parse-time option
/// validators, and the mutation spinner labels.
/// </summary>
public sealed class AddCommandTests
{
    private static Command BuildRoot()
    {
        var services = TestServices.Create();
        var root = TestRoot.With(new AddCommand([], services.State, services.Mutations).Build());
        return root;
    }

    private static ParseResult Parse(params string[] args)
        => BuildRoot().Parse(["add", .. args]);

    // ── -q is --quiet ───────────────────────────────────────────────────────

    [Fact]
    public void BareQ_IsQuiet()
    {
        var result = Parse("Sales/M", "-t", "Measure", "--set", "formatString=0.00", "-q");

        Assert.Empty(result.Errors);
        Assert.True(result.GetValue(GlobalOptions.Quiet));
    }

    [Fact]
    public void RetiredCompatibilityI_IsRejected()
    {
        var result = Parse("Sales/M", "-t", "Measure", "-i", "1");

        Assert.NotEmpty(result.Errors);
    }

    // ── Parse-time validators ───────────────────────────────────────────────

    [Theory]
    [InlineData("--mode", "bogus")]
    [InlineData("--serialization", "bogus")]
    [InlineData("--range-granularity", "fortnight")]
    public void InvalidEnumValues_FailAtParseTime(string option, string value)
    {
        var result = Parse("Sales/M", "-t", "Measure", option, value);

        Assert.Contains(result.Errors, e => e.Message.Contains($"Unknown value for {option}"));
    }

    [Theory]
    [InlineData("--mode", "directquery")]
    [InlineData("--serialization", "TMDL")]
    [InlineData("--range-granularity", "month")]
    public void ValidEnumValues_AreCaseInsensitive(string option, string value)
    {
        var result = Parse("Sales/M", "-t", "Measure", option, value);

        Assert.Empty(result.Errors);
    }

    // ── Spinner labels ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, null, false, false, "Working...")]
    [InlineData(true, null, false, false, "Saving...")]
    [InlineData(false, "out", false, false, "Saving...")]
    [InlineData(false, null, true, false, "Staging...")]
    [InlineData(false, null, false, true, "Reverting...")]
    public void MutationSpinnerLabel_MatchesResolvedMode(bool save, string? saveTo, bool stage, bool revert, string expected)
        => Assert.Equal(expected, MutationSpinnerLabel.For(save, saveTo, stage, revert));
}
