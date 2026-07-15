using Tomix.Cli.Commands;
using Tomix.Cli.Output;

namespace Tomix.Cli.Tests;

public class InteractionGateTests
{
    // The happy path: a human at a text-formatted TTY may be prompted.
    [Fact]
    public void CanPrompt_TextTty_Allowed()
        => Assert.True(InteractionGate.CanPrompt(
            nonInteractive: false, quiet: false, OutputFormats.Text,
            stdinRedirected: false, stderrRedirected: false));

    // Every suppressing condition independently forbids prompting.
    [Theory]
    [InlineData(true, false, OutputFormats.Text, false, false)]   // --non-interactive
    [InlineData(false, true, OutputFormats.Text, false, false)]   // --quiet
    [InlineData(false, false, OutputFormats.Json, false, false)]  // json output
    [InlineData(false, false, OutputFormats.Csv, false, false)]   // csv output
    [InlineData(false, false, OutputFormats.Text, true, false)]   // stdin redirected
    [InlineData(false, false, OutputFormats.Text, false, true)]   // stderr redirected
    public void CanPrompt_SuppressingCondition_Forbidden(
        bool nonInteractive, bool quiet, string format, bool stdinRedirected, bool stderrRedirected)
        => Assert.False(InteractionGate.CanPrompt(nonInteractive, quiet, format, stdinRedirected, stderrRedirected));
}
