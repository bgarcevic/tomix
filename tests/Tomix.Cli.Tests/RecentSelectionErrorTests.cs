using System.Text.Json;
using Tomix.App.State;
using Tomix.Cli.Commands;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

/// <summary>
/// Every way <c>--recent</c> can fail to select a connection reports a documented code on stderr
/// and leaves stdout empty.
/// </summary>
/// <remarks>
/// These failures used to write raw Spectre markup, so under <c>--output-format json</c> one
/// <c>--recent</c> failure emitted a parseable error object and four emitted colored prose — a
/// script parsing stderr succeeded or threw depending on which way the selection failed.
/// <para>
/// Worth stating why this file exists at all: <c>ErrorCodeContractTests</c> only checks that every
/// <c>TOMIX_*</c> literal in <c>src/</c> appears in docs/error-codes.md. It has no docs-to-src
/// direction, so reverting these call sites to raw markup deletes the literals and the whole suite
/// stays green — the doc rows simply become orphans nobody checks. Without the assertions below
/// the fix is unguarded in exactly the direction it regressed from.
/// </para>
/// </remarks>
[Collection(ConsoleStateCollection.Name)]
public sealed class RecentSelectionErrorTests
{
    [Theory]
    // Not a positive index.
    [InlineData(new[] { "--recent", "abc" }, "TOMIX_RECENT_INVALID", 2, true)]
    // Past the end of a populated MRU.
    [InlineData(new[] { "--recent", "99" }, "TOMIX_RECENT_OUT_OF_RANGE", 1, true)]
    // Valueless --recent wants the picker, but json output forbids prompting.
    [InlineData(new[] { "--recent" }, "TOMIX_RECENT_INDEX_REQUIRED", 2, true)]
    // Nothing to select from.
    [InlineData(new[] { "--recent", "1" }, "TOMIX_RECENT_NONE", 1, false)]
    public void RecentSelectionFailure_ReportsItsCodeAsJson(
        string[] recentArgs, string expectedCode, int expectedExit, bool seedRecents)
    {
        var captured = InvokeLs(seedRecents, [.. recentArgs, "--output-format", "json"]);

        Assert.Equal(expectedExit, captured.ExitCode);
        Assert.Equal("", captured.Stdout);

        var error = JsonDocument.Parse(captured.Stderr).RootElement;
        Assert.Equal(expectedCode, error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("hint").GetString()));
    }

    /// <summary>
    /// Text mode keeps prose — the point is that the same failure is reportable either way, not
    /// that JSON leaked into the human output.
    /// </summary>
    [Fact]
    public void RecentSelectionFailure_StaysProseInTextMode()
    {
        var captured = InvokeLs(seedRecents: true, "--recent", "99");

        Assert.Equal(1, captured.ExitCode);
        Assert.Contains("out of range", captured.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("TOMIX_RECENT_OUT_OF_RANGE", captured.Stderr, StringComparison.Ordinal);
    }

    private static ConsoleCapture.Captured InvokeLs(bool seedRecents, params string[] args)
    {
        var services = TestServices.Create();
        if (seedRecents)
            services.State.AddRecentConnection(new CliConnectionState(
                Server: null, Database: null, Model: "/does-not-matter", Auth: null, Local: true, Profile: null));

        var root = TestRoot.With(new LsCommand([], services.State).Build());
        return ConsoleCapture.Invoke(root.Parse(["ls", .. args]));
    }
}
