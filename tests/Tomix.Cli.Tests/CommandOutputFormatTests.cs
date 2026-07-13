using Tomix.Cli.Output;

namespace Tomix.Cli.Tests;

/// <summary>
/// Per-command output-format validation: formats a command cannot render are rejected
/// (exit 2 at the call site) instead of silently falling back to text.
/// </summary>
public sealed class CommandOutputFormatTests
{
    [Theory]
    [InlineData("text")]
    [InlineData("json")]
    [InlineData("auto")]
    public void SupportedFormats_PassValidation(string format)
        => Assert.True(CommandOutput.TryValidateFormat(format, "find", OutputFormats.Text, OutputFormats.Json));

    [Theory]
    [InlineData("csv")]
    [InlineData("tmdl")]
    [InlineData("bim")]
    public void UnsupportedFormats_AreRejected(string format)
        => Assert.False(CommandOutput.TryValidateFormat(format, "find", OutputFormats.Text, OutputFormats.Json));

    [Fact]
    public void InvalidFormat_IsStillRejected()
        => Assert.False(CommandOutput.TryValidateFormat("yaml", "find", OutputFormats.Text, OutputFormats.Json));
}
