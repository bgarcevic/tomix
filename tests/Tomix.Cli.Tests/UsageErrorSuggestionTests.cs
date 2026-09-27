using Tomix.Cli.Commands;

namespace Tomix.Cli.Tests;

/// <summary>
/// A rejected parse prints at most one suggestion, aimed at what was mistyped (issue #180): the
/// suggester used to run once per parse error against the first argument, so "tx st Foo" said
/// "Did you mean 'set'?" three times and "tx set Foo bar baz" suggested the command already in use.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class UsageErrorSuggestionTests
{
    [Theory]
    [InlineData(new[] { "st", "Foo" }, "Did you mean 'set'?")]
    [InlineData(new[] { "sett" }, "Did you mean 'set'?")]
    [InlineData(new[] { "auth", "lgin" }, "Did you mean 'login'?")]
    public void CommandTypo_SuggestsTheSubcommandOnce(string[] args, string suggestion)
    {
        var captured = Report(args);

        Assert.Equal(2, captured.ExitCode);
        Assert.Equal(1, Count(captured.Stderr, "Did you mean"));
        Assert.Contains(suggestion, captured.Stderr);
        Assert.DoesNotContain("was not matched", captured.Stderr);
    }

    [Fact]
    public void ExtraArgument_DoesNotSuggestTheMatchedCommand()
    {
        var captured = Report("set", "Foo", "bar", "baz");

        Assert.Equal(2, captured.ExitCode);
        Assert.Contains("Unrecognized command or argument 'baz'", captured.Stderr);
        Assert.DoesNotContain("Did you mean", captured.Stderr);
        Assert.DoesNotContain("was not matched", captured.Stderr);
    }

    [Theory]
    [InlineData("set", "Foo", "bar", "--forse")]     // unmatched: both positionals are taken
    [InlineData("set", "--forse", "--set", "bar")]   // bound to <path>, next to a parse error
    public void OptionTypo_GetsTheOptionHintDespiteParseErrors(params string[] args)
    {
        var captured = Report(args);

        Assert.Equal(2, captured.ExitCode);
        Assert.Contains("Unrecognized option: --forse", captured.Stderr);
        Assert.Contains("Did you mean '--force'?", captured.Stderr);
        Assert.Equal(1, Count(captured.Stderr, "Did you mean"));
    }

    [Fact]
    public void OptionTypo_CarriesCodeInJson()
    {
        var captured = Report("set", "Foo", "bar", "--forse", "--error-format", "json");

        var error = System.Text.Json.JsonDocument.Parse(captured.Stderr).RootElement;
        Assert.Equal("TOMIX_UNKNOWN_OPTION", error.GetProperty("code").GetString());
    }

    private static ConsoleCapture.Captured Report(params string[] args)
    {
        var parsed = TestRoot.Full().Parse(args);
        Assert.NotEmpty(parsed.Errors);
        return ConsoleCapture.Run(() => UsageErrors.Report(parsed, args));
    }

    private static int Count(string text, string value)
        => (text.Length - text.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;
}
