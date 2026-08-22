using Tomix.Cli.Commands;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;

namespace Tomix.Cli.Tests;

/// <summary>
/// Usage errors keep stdout clean and carry a documented code.
/// </summary>
/// <remarks>
/// <c>tx add -q foo</c> with no matching <c>-i</c> used to report itself with
/// <c>AnsiConsole.MarkupLine</c>, which writes to <b>stdout</b> — so <c>tx add … | jq</c> received
/// colored markup on the data stream instead of the empty stream a failed command owes it
/// (docs/cli-ux-guidelines.md: "Data goes to stdout. Messages … go to stderr"). The exit code was
/// right and the text was right, so nothing caught it. These assert the stream, not the wording.
/// </remarks>
[Collection(ConsoleStateCollection.Name)]
public sealed class UsageErrorStreamTests
{
    private static readonly IReadOnlyList<IModelProvider> Providers = [new TmdlModelProvider()];

    [Fact]
    public void AddWithDanglingProperty_WritesNothingToStdout()
    {
        var captured = InvokeAdd("-t", "measure", "Sales/X", "-q", "formatString");

        Assert.Equal(2, captured.ExitCode);
        Assert.Equal("", captured.Stdout);
        // Text mode renders prose, not the code; the code is pinned by the JSON test below.
        Assert.Contains("has no matching -i value", captured.Stderr);
    }

    [Fact]
    public void AddWithDanglingProperty_HonorsJsonErrorFormat()
    {
        var captured = InvokeAdd(
            "-t", "measure", "Sales/X", "-q", "formatString", "--error-format", "json");

        Assert.Equal(2, captured.ExitCode);
        Assert.Equal("", captured.Stdout);

        var error = System.Text.Json.JsonDocument.Parse(captured.Stderr).RootElement;
        Assert.Equal("TOMIX_ADD_VALUE_REQUIRED", error.GetProperty("code").GetString());
    }

    private static ConsoleCapture.Captured InvokeAdd(params string[] args)
    {
        var services = TestServices.Create();
        var root = TestRoot.With(new AddCommand(Providers, services.State, services.Mutations).Build());

        return ConsoleCapture.Invoke(root.Parse(["add", .. args]), captureAnsiConsole: true);
    }
}
