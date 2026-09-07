using Tomix.App.Validate;
using Tomix.Cli.Output;

namespace Tomix.Cli.Tests;

/// <summary>
/// The validate banner prints the model name carried on the result — never a hardcoded
/// literal — and escapes it, so a name containing markup brackets renders literally instead
/// of being parsed as Spectre markup.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class ValidateRenderTests
{
    [Fact]
    public void Render_Banner_ShowsResultModelName()
    {
        var result = new ValidateModelResult(ModelName: "basic-tmdl", Valid: true, DurationMs: 1, Errors: [], Warnings: []);

        var captured = ConsoleCapture.Run(
            () => ValidateRenderer.Render(result, errorsOnly: false, noMultiline: false, includeBanner: true),
            captureAnsiConsole: true);

        Assert.Contains("Validating: basic-tmdl", captured.Stdout);
        Assert.DoesNotContain("(unnamed)", captured.Stdout);
    }

    [Fact]
    public void Render_EscapesMarkupBrackets_InModelName()
    {
        var result = new ValidateModelResult(ModelName: "Sales [Q1]", Valid: true, DurationMs: 1, Errors: [], Warnings: []);

        var captured = ConsoleCapture.Run(
            () => ValidateRenderer.Render(result, errorsOnly: false, noMultiline: false, includeBanner: true),
            captureAnsiConsole: true);

        Assert.Contains("Validating: Sales [Q1]", captured.Stdout);
    }
}
