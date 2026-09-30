using Tomix.Cli.Commands;
using Tomix.Provider.Tmdl;

namespace Tomix.Cli.Tests;

/// <summary>
/// <c>add --type</c> help names only a few kinds and promises that an invalid value lists them
/// all, so the error must keep that promise.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class AddCommandErrorTests
{
    [Fact]
    public void UnknownType_ListsTheSupportedKinds()
    {
        using var model = SampleModel.CopyToTemp();
        var services = TestServices.Create();
        var root = TestRoot.With(new AddCommand([new TmdlModelProvider()], services.State, services.Mutations).Build());

        var captured = ConsoleCapture.Invoke(
            root.Parse(["add", "Sales/X", "-t", "bogus", "-m", model.Path]), captureAnsiConsole: true);

        Assert.NotEqual(0, captured.ExitCode);
        Assert.Contains("Unknown object type 'bogus'", captured.Stderr);
        Assert.Contains("PolicyRangePartition", captured.Stderr);
    }
}
