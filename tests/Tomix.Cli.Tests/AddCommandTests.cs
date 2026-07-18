using System.CommandLine;
using Tomix.Cli.Commands;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

/// <summary>
/// Parse-level tests for <c>tx add</c>: interleaved <c>-q</c>/<c>-i</c> pairing (including the
/// dangling-<c>-q</c> error), the <c>-q</c>-vs-<c>--quiet</c> split, parse-time option validators,
/// and the mutation spinner labels.
/// </summary>
public sealed class AddCommandTests
{
    private static Command BuildRoot()
    {
        var root = new RootCommand("test");
        foreach (var option in GlobalOptions.All())
            root.Options.Add(option);
        root.Subcommands.Add(new AddCommand([], TestServices.Create()).Build());
        return root;
    }

    private static ParseResult Parse(params string[] args)
        => BuildRoot().Parse(["add", .. args]);

    // ── ParseInterleavedQi ──────────────────────────────────────────────────

    [Fact]
    public void InterleavedQi_PairsPropertiesInOrder()
    {
        var parsed = AddCommand.ParseInterleavedQi(
            Parse("Sales/M", "-t", "Measure", "-i", "1", "-q", "formatString", "-i", "0.00", "-q", "displayFolder", "-i", "KPIs"));

        Assert.Equal("1", parsed.PrimaryValue);
        Assert.Collection(parsed.Properties,
            p => { Assert.Equal("formatString", p.Property); Assert.Equal("0.00", p.Value); },
            p => { Assert.Equal("displayFolder", p.Property); Assert.Equal("KPIs", p.Value); });
        Assert.Null(parsed.DanglingProperty);
    }

    [Fact]
    public void InterleavedQi_PrimaryValueAfterProperties()
    {
        var parsed = AddCommand.ParseInterleavedQi(
            Parse("Sales/M", "-t", "Measure", "-q", "description", "-i", "d", "-i", "1"));

        Assert.Equal("1", parsed.PrimaryValue);
        var property = Assert.Single(parsed.Properties);
        Assert.Equal("description", property.Property);
    }

    [Fact]
    public void TrailingDanglingQ_IsReported()
    {
        var parsed = AddCommand.ParseInterleavedQi(
            Parse("Sales/M", "-t", "Measure", "-i", "1", "-q", "formatString"));

        Assert.Equal("formatString", parsed.DanglingProperty);
    }

    [Fact]
    public void MidStreamDanglingQ_IsReported()
    {
        var parsed = AddCommand.ParseInterleavedQi(
            Parse("Sales/M", "-t", "Measure", "-q", "formatString", "--save", "-i", "1"));

        Assert.Equal("formatString", parsed.DanglingProperty);
    }

    [Fact]
    public void ConsecutiveQ_ReportsAbandonedFirstProperty()
    {
        var parsed = AddCommand.ParseInterleavedQi(
            Parse("Sales/M", "-t", "Measure", "-q", "a", "-q", "b", "-i", "1"));

        Assert.Equal("a", parsed.DanglingProperty);
    }

    // ── -q vs --quiet ───────────────────────────────────────────────────────

    [Fact]
    public void BareQ_IsPropertyOption_AndQuietStillParses()
    {
        var result = Parse("Sales/M", "-t", "Measure", "-q", "formatString", "-i", "0.00", "--quiet");

        Assert.Empty(result.Errors);
        Assert.True(result.GetValue(GlobalOptions.Quiet));
        var parsed = AddCommand.ParseInterleavedQi(result);
        var property = Assert.Single(parsed.Properties);
        Assert.Equal("formatString", property.Property);
    }

    [Fact]
    public void QuietOption_HasNoShortAlias()
        => Assert.DoesNotContain("-q", GlobalOptions.Quiet.Aliases);

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
