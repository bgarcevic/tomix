using System.CommandLine;
using Tomix.Cli.Commands;

namespace Tomix.Cli.Tests;

/// <summary>
/// Parse-level tests for <c>tx mv</c>: the <c>move</c>/<c>rename</c> aliases must route to the
/// same command so scripts can use whichever verb reads best.
/// </summary>
public sealed class MvCommandTests
{
    private static Command BuildRoot()
    {
        var services = TestServices.Create();
        var root = TestRoot.With(new MvCommand([], services.State, services.Mutations).Build());
        return root;
    }

    [Theory]
    [InlineData("mv")]
    [InlineData("move")]
    [InlineData("rename")]
    public void MvAndItsAliases_ParseToTheSameCommand(string verb)
    {
        var result = BuildRoot().Parse([verb, "Sales/Old", "Sales/New"]);

        Assert.Empty(result.Errors);
        Assert.Equal("mv", result.CommandResult.Command.Name);
    }
}
