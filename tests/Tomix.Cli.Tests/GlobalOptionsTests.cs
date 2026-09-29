using Tomix.Cli.Commands;

namespace Tomix.Cli.Tests;

/// <summary>
/// The <c>-q</c> alias belongs to the global <c>--quiet</c> flag on every command, including
/// <c>add</c>/<c>set</c> now that their compatibility <c>-q</c>/<c>-i</c> form is retired.
/// </summary>
public sealed class GlobalOptionsTests
{
    [Fact]
    public void QuietAlias_BindsGlobalQuiet_OnCommandsWithoutLocalQ()
    {
        var services = TestServices.Create();
        var root = TestRoot.With(new LsCommand([], services.State).Build());

        var result = root.Parse(["ls", "Sales", "-q"]);

        Assert.Empty(result.Errors);
        Assert.True(result.GetValue(GlobalOptions.Quiet));
    }

    [Fact]
    public void QuietAlias_BindsGlobalQuiet_OnAdd()
    {
        var services = TestServices.Create();
        var root = TestRoot.With(new AddCommand([], services.State, services.Mutations).Build());

        var result = root.Parse(["add", "Sales/M", "-t", "Measure", "-e", "1", "-q"]);

        Assert.Empty(result.Errors);
        Assert.True(result.GetValue(GlobalOptions.Quiet));
    }
}
