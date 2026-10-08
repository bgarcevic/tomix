using System.Text.RegularExpressions;
using Spectre.Console;
using Tomix.App.Get;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

/// <summary>
/// The hidden-row contract of the ls tables: a hidden object's whole row is muted (dim), so
/// hidden objects read at a glance instead of only their grey "True" cell. Expression cells
/// additionally carry DAX syntax highlighting on visible rows. Both are asserted on the
/// ANSI escape sequences because markup is consumed before the writer sees it.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class LsRendererTests
{
    private const string Dim = "\x1b[2m";       // muted
    private const string Function = "\x1b[38;5;12m";  // functions (bright blue)
    private const string Keyword = "\x1b[38;5;5m";   // keywords (magenta)
    private const string Reference = "\x1b[38;5;6m"; // measure references (cyan)
    private const string Literal = "\x1b[38;5;2m";   // strings and numbers (green)

    [Fact]
    public void HiddenTable_MutesEveryCell()
    {
        var output = RenderTables(Table("Secret", hidden: true));

        var row = RowLine(output, "Secret");
        Assert.Contains(Dim + "Secret", row);
        Assert.Contains(Dim + "hush", row);
        Assert.Contains(Dim + "True", row);
    }

    [Fact]
    public void VisibleTable_KeepsCellsUnstyled()
    {
        var output = RenderTables(Table("Open", hidden: false));

        var row = RowLine(output, "Open");
        Assert.DoesNotContain(Dim + "Open", row);
        Assert.DoesNotContain(Dim + "loud", row);
    }

    [Fact]
    public void MixedTables_OnlyHiddenRowIsMuted()
    {
        var output = RenderTables(Table("Secret", hidden: true), Table("Open", hidden: false));

        Assert.Contains(Dim + "Secret", RowLine(output, "Secret"));
        Assert.DoesNotContain(Dim + "Open", RowLine(output, "Open"));
    }

    [Fact]
    public void ColumnCount_SumsDataAndCalculatedColumns()
    {
        var output = RenderTables(Table("Date", hidden: false, calculatedColumns: 3));

        var cells = TableRowCells(output, "Date");
        Assert.Equal("Date", cells[1]);
        Assert.Equal("10", cells[2]);
    }

    [Fact]
    public void MeasureExpression_IsHighlighted()
    {
        var output = RenderTables(Measure("Sales", "SUM('Sales'[Amount])"));

        Assert.Contains(Function + "SUM", output);
        Assert.Contains("('Sales'[Amount])", output);
    }

    [Fact]
    public void HiddenMeasure_MutesExpression()
    {
        var output = RenderTables(Measure("Secret", "SUM('Sales'[Amount])", hidden: true));

        Assert.Contains(Dim + "SUM('Sales'[Amount])", output);
        Assert.DoesNotContain(Function, output);
    }

    [Fact]
    public void MeasureExpressionPreviewNote_StaysPlain()
    {
        var output = RenderMultiline(Measure("Sales", "SUM('Sales'[Amount])\n- [Qty]\n- [Price]\n- [Tax]"));

        Assert.Contains("... (+1 line)", output);
        // No literal in the cell or the suffix, so the literal color must not appear at all.
        Assert.DoesNotContain(Literal, output);
    }

    [Fact]
    public void MPartitionExpression_IsHighlighted()
    {
        var output = RenderTables(Partition("Orders", detail: "import", "let Source = Sql.Database(\"srv\") in Source"));

        Assert.Contains(Keyword + "let", output);
        Assert.Contains(" Source = ", output);
        Assert.Contains(Function + "Sql.Database", output);
        Assert.Contains("let Source = Sql.Database(\"srv\") in Source", AnsiCodes.Replace(output, ""));
    }

    [Fact]
    public void CalculatedPartitionExpression_IsHighlighted()
    {
        var output = RenderTables(Partition("SalesCalc", detail: "calculated", "ROW(\"A\", 1)"));

        Assert.Contains(Function + "ROW", output);
    }

    [Fact]
    public void CalculationItemExpression_IsHighlighted()
    {
        var output = RenderTables(CalculationItem("YoY", "CALCULATE('Sales'[Amount])"));

        Assert.Contains(Function + "CALCULATE", output);
        Assert.Contains("('Sales'[Amount])", output);
    }

    [Fact]
    public void MeasureReference_IsColoredApartFromColumns()
    {
        var measures = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Profit", "Cost" };
        var output = RenderWith(measures, Measure("Profit", "DIVIDE([Profit], [Cost]) + [Qty]"));

        Assert.Contains(Reference + "[Profit]", output);
        Assert.Contains(Reference + "[Cost]", output);
        // An unqualified bracket that resolves to no measure stays a column, which is plain.
        Assert.DoesNotContain(Reference + "[Qty]", output);
    }

    [Fact]
    public void RedirectedStdout_IsUnwrappedWithAsciiBorders()
    {
        const string expression = "DIVIDE(SUMX('Opportunities', 'Opportunities'[Product Revenue] * 'Opportunities'[Factor]), [Revenue])";
        var captured = ConsoleCapture.Run(
            () =>
            {
                StdOut.PlainWhenRedirected(AnsiConsole.Console);
                Assert.Equal(TableBorder.Ascii, Styling.Border);
                LsRenderer.Render(
                    new GetListResult("Sample", 1550, [Measure("Factored Share", expression)], null),
                    pathsOnly: false,
                    noMultiline: false);
            },
            captureAnsiConsole: true);

        var output = AnsiCodes.Replace(captured.Stdout, "");
        Assert.Contains(expression, output);
        Assert.Matches(@"^\| Factored Share +\|", RowLine(output, "Factored Share"));
        Assert.DoesNotContain('│', output);
    }

    private static string RenderTables(params GetListObject[] objects) => Render(objects, noMultiline: true);

    private static string RenderMultiline(params GetListObject[] objects) => Render(objects, noMultiline: false);

    private static string RenderWith(IReadOnlySet<string> measureNames, params GetListObject[] objects)
        => Render(objects, noMultiline: true, measureNames: measureNames);

    private static string Render(GetListObject[] objects, bool noMultiline, IReadOnlySet<string>? measureNames = null)
    {
        var captured = ConsoleCapture.Run(
            () =>
            {
                LsRenderer.Render(
                    new GetListResult("Sample", 1550, objects, measureNames),
                    pathsOnly: false,
                    noMultiline: noMultiline);
                return 0;
            },
            captureAnsiConsole: true,
            forceAnsi: true);
        Assert.Equal(0, captured.ExitCode);
        return captured.Stdout;
    }

    private static GetListObject Table(string name, bool hidden, int calculatedColumns = 0) => new(
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
            [ModelObjectKind.CalculatedColumn] = calculatedColumns,
            [ModelObjectKind.Measure] = 0,
            [ModelObjectKind.Partition] = 1
        },
        Projected: new Dictionary<string, object?>());

    private static GetListObject Measure(string name, string expression, bool hidden = false) => new(
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

    private static GetListObject Partition(string name, string detail, string expression) => new(
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

    private static GetListObject CalculationItem(string name, string expression) => new(
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

    /// <summary>
    /// The row matching <paramref name="name"/> split into cell texts: border color codes and
    /// the rounded-border glyphs are stripped, remaining cells trimmed. Index 0 is the empty
    /// leading segment; cell 1 is the row's first column.
    /// </summary>
    private static string[] TableRowCells(string output, string name)
        => AnsiCodes
            .Replace(RowLine(output, name), "")
            .Split('│')
            .Select(cell => cell.Trim())
            .ToArray();

    private static readonly Regex AnsiCodes = new(@"\x1b\[[0-9;]*m", RegexOptions.Compiled);
}
