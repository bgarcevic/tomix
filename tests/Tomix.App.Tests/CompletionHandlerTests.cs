using Tomix.App.Completion;

namespace Tomix.App.Tests;

public sealed class CompletionHandlerTests
{
    private static readonly IReadOnlyList<string> Commands = ["doctor", "config", "completion", "load", "ls"];

    [Theory]
    [InlineData("bash")]
    [InlineData("zsh")]
    [InlineData("fish")]
    [InlineData("powershell")]
    [InlineData("PowerShell")]
    [InlineData("pwsh")]
    public void Generate_SupportedShell_ReturnsDynamicSuggestionScript(string shell)
    {
        var result = new CompletionHandler().Generate(shell, Commands);

        Assert.True(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Data!.Script));
        Assert.Contains("tx completion", result.Data.Script);
        Assert.Contains("[suggest:", result.Data.Script);
        Assert.DoesNotContain("doctor", result.Data.Script);
    }

    [Theory]
    [InlineData("PowerShell", "powershell")]
    [InlineData("pwsh", "powershell")]
    public void Generate_NormalizesShellName(string shell, string expected)
    {
        var result = new CompletionHandler().Generate(shell, Commands);

        Assert.True(result.Success);
        Assert.Equal(expected, result.Data!.Shell);
    }

    [Fact]
    public void Generate_UnsupportedShell_FailsWithExitCode2()
    {
        var result = new CompletionHandler().Generate("tcsh", Commands);

        Assert.False(result.Success);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("TOMIX_COMPLETION_UNSUPPORTED_SHELL", result.Diagnostics[0].Code);
        Assert.Contains("Argument 'tcsh' not recognized", result.Diagnostics[0].Message);
        Assert.Contains("'pwsh'", result.Diagnostics[0].Message);
    }
}
