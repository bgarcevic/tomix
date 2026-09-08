using Tomix.App.Ls;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

/// <summary>
/// The hidden-row contract of the ls tables: a hidden object's whole row is muted (Slate), so
/// hidden objects read at a glance instead of only their grey "True" cell. Expression cells
/// additionally carry DAX syntax highlighting on visible rows. Both are asserted on the
/// true-color escape sequences because markup is consumed before the writer sees it.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class LsRendererTests
{
    private const string Slate = "\x1b[38;2;117;127;136m";
    private const string Harbor = "\x1b[38;2;69;130;172m";  // functions
    private const string Sage = "\x1b[38;2;52;137;126m";    // table names
    private const string Moss = "\x1b[38;2;64;129;57m";     // column references
    private const string Orchid = "\x1b[38;2;207;103;172m"; // measure references

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

    [Fact]
    public void MeasureExpression_IsHighlighted()
    {
        var output = RenderTables(Measure("Sales", "SUM('Sales'[Amount])"));

        Assert.Contains(Harbor + "SUM", output);
        Assert.Contains(Sage + "'Sales'", output);
    }

    [Fact]
    public void HiddenMeasure_MutesExpression()
    {
        var output = RenderTables(Measure("Secret", "SUM('Sales'[Amount])", hidden: true));

        Assert.Contains(Slate + "SUM('Sales'[Amount])", output);
        Assert.DoesNotContain(Harbor, output);
    }

    [Fact]
    public void MeasureExpressionPreviewNote_StaysPlain()
    {
        var output = RenderMultiline(Measure("Sales", "SUM('Sales'[Amount])\n- [Qty]\n- [Price]\n- [Tax]"));

        Assert.Contains("... (+1 line)", output);
        // No literal in the cell or the suffix, so Amber (strings/numbers) must not appear at all.
        Assert.DoesNotContain("\x1b[38;2;176;126;42m", output);
    }

    [Fact]
    public void MPartitionExpression_StaysPlain()
    {
        var output = RenderTables(Partition("Orders", detail: "import", "let Source = 1 in Source"));

        Assert.Contains("let Source = 1 in Source", output);
        Assert.DoesNotContain(Harbor, output);
    }

    [Fact]
    public void CalculatedPartitionExpression_IsHighlighted()
    {
        var output = RenderTables(Partition("SalesCalc", detail: "calculated", "ROW(\"A\", 1)"));

        Assert.Contains(Harbor + "ROW", output);
    }

    [Fact]
    public void CalculationItemExpression_IsHighlighted()
    {
        var output = RenderTables(CalculationItem("YoY", "CALCULATE('Sales'[Amount])"));

        Assert.Contains(Harbor + "CALCULATE", output);
        Assert.Contains(Sage + "'Sales'", output);
    }

    [Fact]
    public void MeasureReference_IsColoredApartFromColumns()
    {
        var measures = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Profit", "Cost" };
        var output = RenderWith(measures, Measure("Profit", "DIVIDE([Profit], [Cost]) + [Qty]"));

        Assert.Contains(Orchid + "[Profit]", output);
        Assert.Contains(Orchid + "[Cost]", output);
        // An unqualified bracket that resolves to no measure stays a column.
        Assert.Contains(Moss + "[Qty]", output);
    }

    private static string RenderTables(params LsObject[] objects) => Render(objects, noMultiline: true);

    private static string RenderMultiline(params LsObject[] objects) => Render(objects, noMultiline: false);

    private static string RenderWith(IReadOnlySet<string> measureNames, params LsObject[] objects)
        => Render(objects, noMultiline: true, measureNames: measureNames);

    private static string Render(LsObject[] objects, bool noMultiline, IReadOnlySet<string>? measureNames = null)
    {
        var captured = ConsoleCapture.Run(
            () =>
            {
                LsRenderer.Render(
                    new LsModelResult("Sample", 1550, objects, measureNames),
                    pathsOnly: false,
                    noMultiline: noMultiline);
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

    private static LsObject Measure(string name, string expression, bool hidden = false) => new(
        Path: $"Sales/{name}",
        Name: name,
        Kind: ModelObjectKind.Measure,
        Detail: null,
        Expression: expression,
        Description: null,
        Hidden: hidden,
        SourceColumn: null,
        ChildCounts: new Dictionary<ModelObjectKind, int>(),
        Projected: new Dictionary<string, object?>());

    private static LsObject Partition(string name, string detail, string expression) => new(
        Path: $"Sales/{name}",
        Name: name,
        Kind: ModelObjectKind.Partition,
        Detail: detail,
        Expression: expression,
        Description: null,
        Hidden: false,
        SourceColumn: null,
        ChildCounts: new Dictionary<ModelObjectKind, int>(),
        Projected: new Dictionary<string, object?>());

    private static LsObject CalculationItem(string name, string expression) => new(
        Path: $"CalcGroup/{name}",
        Name: name,
        Kind: ModelObjectKind.CalculationItem,
        Detail: null,
        Expression: expression,
        Description: null,
        Hidden: false,
        SourceColumn: null,
        ChildCounts: new Dictionary<ModelObjectKind, int>(),
        Projected: new Dictionary<string, object?>());

    private static string RowLine(string output, string name)
        => output.Split('\n').Single(line => line.Contains(name));
}
