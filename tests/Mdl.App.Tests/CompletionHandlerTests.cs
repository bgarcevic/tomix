using Mdl.App.Completion;

namespace Mdl.App.Tests;

public sealed class CompletionHandlerTests
{
    private static readonly IReadOnlyList<string> Commands = ["doctor", "config", "completion", "info", "ls"];

    [Theory]
    [InlineData("bash")]
    [InlineData("zsh")]
    [InlineData("fish")]
    [InlineData("powershell")]
    [InlineData("PowerShell")]
    public void Generate_SupportedShell_ReturnsScriptContainingCommands(string shell)
    {
        var result = new CompletionHandler().Generate(shell, Commands);

        Assert.True(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Data!.Script));
        Assert.Contains("doctor", result.Data.Script);
        Assert.Contains("config", result.Data.Script);
    }

    [Fact]
    public void Generate_NormalizesShellName()
    {
        var result = new CompletionHandler().Generate("PowerShell", Commands);

        Assert.True(result.Success);
        Assert.Equal("powershell", result.Data!.Shell);
    }

    [Fact]
    public void Generate_UnsupportedShell_FailsWithExitCode2()
    {
        var result = new CompletionHandler().Generate("tcsh", Commands);

        Assert.False(result.Success);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("MDL_COMPLETION_UNSUPPORTED_SHELL", result.Diagnostics[0].Code);
    }
}
