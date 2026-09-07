using Tomix.App.Ls;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

/// <summary>
/// The hidden-row contract of the ls tables: a hidden object's whole row is muted (Slate), so
/// hidden objects read at a glance instead of only their grey "True" cell. Asserted on the
/// true-color escape sequence because markup is consumed before the writer sees it.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class LsRendererTests
{
    private const string Slate = "\x1b[38;2;118;128;137m";

    [Fact]
    public void HiddenTable_MutesEveryCell()
    {
        var output = RenderTables(Table("Secret", hidden: true));

        var row = RowLine(output, "Secret");
        Assert.Contains(Slate + "Secret", row);
        Assert.Contains(Slate + "hush", row);
        Assert.Contains(Slate + "True", row);
    }

    [Fact]
    public void VisibleTable_KeepsCellsUnstyled()
    {
        var output = RenderTables(Table("Open", hidden: false));

        var row = RowLine(output, "Open");
        Assert.DoesNotContain(Slate + "Open", row);
        Assert.DoesNotContain(Slate + "loud", row);
    }

    [Fact]
    public void MixedTables_OnlyHiddenRowIsMuted()
    {
        var output = RenderTables(Table("Secret", hidden: true), Table("Open", hidden: false));

        Assert.Contains(Slate + "Secret", RowLine(output, "Secret"));
        Assert.DoesNotContain(Slate + "Open", RowLine(output, "Open"));
    }

    private static string RenderTables(params LsObject[] objects)
    {
        var captured = ConsoleCapture.Run(
            () =>
            {
                LsRenderer.Render(
                    new LsModelResult("Sample", 1550, objects),
                    pathsOnly: false,
                    noMultiline: true);
                return 0;
            },
            captureAnsiConsole: true,
            forceAnsi: true);
        Assert.Equal(0, captured.ExitCode);
        return captured.Stdout;
    }

    private static LsObject Table(string name, bool hidden) => new(
        Path: $"Tables/{name}",
        Name: name,
        Kind: ModelObjectKind.Table,
        Detail: null,
        Expression: null,
        Description: hidden ? "hush" : "loud",
        Hidden: hidden,
        SourceColumn: null,
        ChildCounts: new Dictionary<ModelObjectKind, int>
        {
            [ModelObjectKind.Column] = 7,
            [ModelObjectKind.Measure] = 0,
            [ModelObjectKind.Partition] = 1
        },
        Projected: new Dictionary<string, object?>());

    private static string RowLine(string output, string name)
        => output.Split('\n').Single(line => line.Contains(name));
}
