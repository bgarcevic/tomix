using Tomix.App.Validate;
using Tomix.Cli.Output;

namespace Tomix.Cli.Tests;

/// <summary>
/// The <c>--ci</c> projection for validate: every issue annotates at its own severity, so
/// warnings emit as warnings (github <c>::warning::</c> / vsts <c>type=warning</c>) and only
/// errors trigger the vsts Failed trailer. In the console-state collection because EmitCi
/// writes to <c>Console.Error</c>.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class ValidateCiProjectionTests
{
    private static ValidateModelResult Result(
        ValidationIssue[]? errors = null,
        ValidationIssue[]? warnings = null)
        => new(
            ModelName: "basic-tmdl",
            Valid: (errors ?? []).Length == 0,
            DurationMs: 1,
            Errors: errors ?? [],
            Warnings: warnings ?? []);

    private static ValidationIssue Issue(ValidationSeverity severity, string code, string message)
        => new(severity, code, message, "Sales[Total]", Expression: null);

    [Fact]
    public void EmitCi_Github_AnnotatesAtIssueSeverity()
    {
        var result = Result(
            errors: [Issue(ValidationSeverity.Error, "DAX0001", "Table 'X' cannot be found.")],
            warnings: [Issue(ValidationSeverity.Warning, "DAX0003", "Measure or column [Y] cannot be found.")]);

        var captured = ConsoleCapture.Run(() => ValidateRenderer.EmitCi("github", result));

        var lines = captured.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("::error::Table 'X' cannot be found. [Sales[Total]] (DAX0001)", lines[0]);
        Assert.StartsWith("::warning::Measure or column [Y] cannot be found. [Sales[Total]] (DAX0003)", lines[1]);
    }

    [Fact]
    public void EmitCi_Vsts_WarningsOnly_OmitsFailedTrailer()
    {
        // Warnings annotate at warning level; without an error the task is not failed.
        var result = Result(warnings: [Issue(ValidationSeverity.Warning, "DAX0003", "Loose reference.")]);

        var captured = ConsoleCapture.Run(() => ValidateRenderer.EmitCi("vsts", result));

        Assert.Contains("##vso[task.logissue type=warning;]Loose reference.", captured.Stderr);
        Assert.DoesNotContain("task.complete", captured.Stderr);
    }

    [Fact]
    public void EmitCi_CleanModel_EmitsNothing()
    {
        var captured = ConsoleCapture.Run(() => ValidateRenderer.EmitCi("github", Result()));

        Assert.Equal("", captured.Stderr);
    }
}
