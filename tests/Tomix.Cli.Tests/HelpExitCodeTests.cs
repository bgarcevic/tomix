using System.CommandLine;
using Tomix.App.Format;

namespace Tomix.Cli.Tests;

/// <summary>
/// Live-model QA finding: every command with required positional arguments (mv, add, rm, set,
/// cp, get, find, ...) exited 2 on <c>--help</c> because the Spectre help action did not clear
/// the missing-argument parse errors the way the built-in HelpAction does. Help must exit 0 on
/// every command so <c>tx &lt;cmd&gt; --help &amp;&amp; ...</c> scripting works.
/// </summary>
// Renders help through the global AnsiConsole; joins the console-state collection so it never
// runs concurrently with tests that swap Console.Out (e.g. GetLsParityTests), which would
// otherwise capture the root-help banner in their redirected stdout.
[Collection(ConsoleStateCollection.Name)]
public sealed class HelpExitCodeTests
{

    [Fact]
    public void EveryCommand_Help_ParsesCleanAndExitsZero()
    {
        var root = TestRoot.Full();
        var failures = new List<string>();

        foreach (var (_, path) in TestRoot.Descendants(root, includeHidden: true))
        {
            var result = root.Parse([.. path, "--help"]);
            // Mirrors Program.Main: any parse error is a usage error and exits 2.
            var exitCode = result.Errors.Count > 0 ? 2 : result.Invoke();
            if (exitCode != 0)
                failures.Add(
                    $"tx {string.Join(' ', path)} --help -> exit {exitCode}" +
                    $" ({string.Join("; ", result.Errors.Select(e => e.Message))})");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void RootHelp_ExitsZero()
    {
        var result = TestRoot.Full().Parse(["--help"]);

        Assert.Empty(result.Errors);
        Assert.Equal(0, result.Invoke());
    }

    [Fact]
    public void MissingRequiredArgument_WithoutHelp_StillFailsParse()
    {
        var result = TestRoot.Full().Parse(["mv"]);

        Assert.NotEmpty(result.Errors);
    }

}
