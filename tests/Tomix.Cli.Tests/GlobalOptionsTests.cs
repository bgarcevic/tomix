using Tomix.Cli.Commands;

namespace Tomix.Cli.Tests;

/// <summary>
/// The <c>-q</c> alias belongs to the global <c>--quiet</c> flag. The only exceptions are
/// <c>add</c>/<c>set</c>, whose local compatibility <c>-q</c> (property name) shadows the global
/// alias until the bare form retires at 1.0 — so <c>-q</c> keeps exactly one meaning per command.
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
    public void QuietAlias_IsShadowedByAddCompatQ()
    {
        var services = TestServices.Create();
        var root = TestRoot.With(new AddCommand([], services.State, services.Mutations).Build());

        var result = root.Parse(["add", "Sales/M", "-t", "Measure", "-q", "description", "-i", "d", "-i", "1"]);

        Assert.Empty(result.Errors);
        Assert.False(result.GetValue(GlobalOptions.Quiet));
    }
}
