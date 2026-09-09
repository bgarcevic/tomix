using System.CommandLine;
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
}
