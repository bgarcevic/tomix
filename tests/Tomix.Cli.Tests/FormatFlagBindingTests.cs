using System.CommandLine;
using System.Text.Json;
using Tomix.App.Format;
using Tomix.Cli.Commands;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;

namespace Tomix.Cli.Tests;

/// <summary>
/// Pins the CLI-to-request wiring of format's lifecycle flags. Regression: <c>--dry-run</c> was
/// inserted positionally into the request construction and bound to <see cref="FormatModelRequest"/>'s
/// <c>Stage</c> slot, so a dry-run staged the mutation and <c>--stage</c> reverted it — none of the
/// parse-level or handler-level tests could see it, because the misalignment lived in the command's
/// argument construction. These tests invoke the real command against the sample model and assert
/// the rendered outcome matches the flags that were passed.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed partial class FormatFlagBindingTests
{
    private static readonly IReadOnlyList<IModelProvider> Providers = [new TmdlModelProvider()];
    private static readonly string SampleTmdl = SampleModel.Locate();

    private static RootCommand BuildRoot()
    {
        var services = TestServices.Create();
        return TestRoot.With(new FormatCommand(
            Providers, new StubFormatter(), services.State, services.Mutations).Build());
    }

    [Fact]
    public void DryRun_RendersPreview_DoesNotStage()
    {
        var captured = ConsoleCapture.Invoke(
            BuildRoot().Parse(["format", "-m", SampleTmdl, "--dry-run"]),
            captureAnsiConsole: true);

        Assert.Equal(0, captured.ExitCode);
        var output = StripAnsi(captured.Stdout);
        Assert.Contains("Formatted: 4", output);
        Assert.Contains("Dry run: nothing was saved.", output);
        Assert.DoesNotContain("Mutation staged.", output);
    }

    [Fact]
    public void Stage_StagesRatherThanReverts()
    {
        var captured = ConsoleCapture.Invoke(
            BuildRoot().Parse(["format", "-m", SampleTmdl, "--stage"]),
            captureAnsiConsole: true);

        Assert.Equal(0, captured.ExitCode);
        Assert.Contains("Mutation staged.", StripAnsi(captured.Stdout));
        Assert.DoesNotContain("Nothing is staged", StripAnsi(captured.Stderr));
    }

    [Fact]
    public void SweepFailure_TextOutput_PrintsErrorDetailToStderr()
    {
        // Issue #200: a uniform sweep failure printed only "Failed: 4" and hid the formatter's
        // HTTP error; the detail now goes to stderr, deduplicated with the affected-object count.
        var captured = ConsoleCapture.Invoke(
            BuildFailingRoot().Parse(["format", "-m", SampleTmdl, "--dry-run"]),
            captureAnsiConsole: true);

        Assert.Equal(0, captured.ExitCode);
        Assert.Contains("Failed: 4", StripAnsi(captured.Stdout));
        var stderr = StripAnsi(captured.Stderr);
        Assert.Contains("HTTP 415", stderr);
        Assert.Contains("(+3 more)", stderr);
        Assert.Equal(1, stderr.Split("HTTP 415").Length - 1);
    }

    [Fact]
    public void SweepFailure_JsonOutput_CarriesErrorPerFailedResult()
    {
        var captured = ConsoleCapture.Invoke(
            BuildFailingRoot().Parse(["format", "-m", SampleTmdl, "--dry-run", "--output-format", "json"]),
            captureAnsiConsole: true);

        Assert.Equal(0, captured.ExitCode);
        using var document = JsonDocument.Parse(captured.Stdout);
        var failed = document.RootElement
            .GetProperty("data")
            .GetProperty("results")
            .EnumerateArray()
            .Where(r => r.GetProperty("status").GetString() == "failed")
            .ToList();

        Assert.Equal(4, failed.Count);
        Assert.All(failed, r => Assert.Contains("HTTP 415", r.GetProperty("error").GetString()));
    }

    private static RootCommand BuildFailingRoot()
    {
        var services = TestServices.Create();
        return TestRoot.With(new FormatCommand(
            Providers, new FailingStubFormatter(), services.State, services.Mutations).Build());
    }

    [System.Text.RegularExpressions.GeneratedRegex("\x1b\\[[0-9;]*m")]
    private static partial System.Text.RegularExpressions.Regex AnsiRegex();

    private static string StripAnsi(string text) => AnsiRegex().Replace(text, "");

    /// <summary>
    /// Succeeds with a changed expression so the mutation runs through the lifecycle (any
    /// formatting failure takes the handler's failed short-circuit, which renders no
    /// lifecycle outcome).
    /// </summary>
    private sealed class StubFormatter : IExpressionFormatterClient
    {
        public bool CanFormat(string language) => true;

        public Task<ExpressionFormatResponse> FormatAsync(
            ExpressionFormatRequest request,
            CancellationToken cancellationToken)
            => Task.FromResult(new ExpressionFormatResponse(
                true, request.Expression + " ", []));
    }

    private sealed class FailingStubFormatter : IExpressionFormatterClient
    {
        public bool CanFormat(string language) => true;

        public Task<ExpressionFormatResponse> FormatAsync(
            ExpressionFormatRequest request,
            CancellationToken cancellationToken)
            => Task.FromResult(new ExpressionFormatResponse(
                false,
                request.Expression,
                ["Formatter service returned HTTP 415: unsupported media type"]));
    }
}
