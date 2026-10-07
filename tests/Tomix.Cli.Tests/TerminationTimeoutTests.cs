using Tomix.App.Format;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;
using Tomix.Provider.Tom;

namespace Tomix.Cli.Tests;

/// <summary>
/// Which commands System.CommandLine ends two seconds after Ctrl+C. The commands that hold a
/// session handle Ctrl+C themselves: with the library's handling on as well, a Ctrl+C meant for
/// the running command in <c>tx interactive</c> also cancelled the session and ended the process
/// with 130, discarding unsaved changes without asking.
/// </summary>
public sealed class TerminationTimeoutTests
{
    private static readonly IReadOnlyList<IModelProvider> Providers = [new TmdlModelProvider(), new TomFileModelProvider()];

    [Theory]
    [InlineData("interactive")]
    [InlineData("shell")]
    [InlineData("serve")]
    [InlineData("ui")]
    [InlineData("mcp")]
    public void SessionCommands_HandleCtrlCThemselves(string command)
        => Assert.Null(Program.TerminationTimeout(Parse(command)));

    [Theory]
    [InlineData("query")]
    [InlineData("deploy")]
    [InlineData("refresh")]
    public void OtherCommands_EndTwoSecondsAfterCtrlC(string command)
        => Assert.Equal(TimeSpan.FromSeconds(2), Program.TerminationTimeout(Parse(command)));

    private static System.CommandLine.ParseResult Parse(string command)
        => Program.BuildRootCommand(Providers, new CompositeExpressionFormatterClient([]), TestRoot.Version, TestServices.Create()).Parse([command]);
}
