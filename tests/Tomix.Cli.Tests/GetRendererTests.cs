using Tomix.App.Get;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

/// <summary>
/// The get properties view highlights DAX-bearing values and nothing else. Asserted on the
/// true-color escape sequences because markup is consumed before the writer sees it.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class GetRendererTests
{
    private const string Harbor = "\x1b[38;2;69;130;172m";  // functions
    private const string Sage = "\x1b[38;2;52;137;126m";    // table names
    private const string Moss = "\x1b[38;2;64;129;57m";     // column references
    private const string Orchid = "\x1b[38;2;207;103;172m"; // measure references

    [Fact]
    public void DaxExpression_IsHighlighted()
    {
        var measure = new ModelObject("Cost", ModelObjectKind.Measure, "Sales/Cost",
            Detail: null, Expression: "SUM('Sales'[Total Product Cost])", Description: null,
            Hidden: false, SourceColumn: null, Children: []);

        var output = Render(measure);

        Assert.Contains(Harbor + "SUM", output);
        Assert.Contains(Sage + "'Sales'", output);
    }

    [Fact]
    public void MeasureReference_IsColoredApartFromColumns()
    {
        const string dax = "DIVIDE([Profit], 'Sales'[Qty])";
        var measure = new ModelObject("Profit %", ModelObjectKind.Measure, "Sales/Profit %",
            Detail: null, Expression: dax, Description: null,
            Hidden: false, SourceColumn: null, Children: []);
        var measureNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Profit" };

        var output = Render(measure, measureNames: measureNames);

        Assert.Contains(Orchid + "[Profit]", output);
        Assert.Contains(Moss + "[Qty]", output);
    }

    [Fact]
    public void MPartitionExpression_StaysPlain()
    {
        const string m = "Table.SelectRows(Source, each [X] > 0)";
        var partition = new ModelObject("Part", ModelObjectKind.Partition, "Sales/Part",
            Detail: "m", Expression: m, Description: null, Hidden: false,
            SourceColumn: null, Children: [],
            Properties: new Dictionary<string, string> { ["PartitionSourceType"] = "M" });

        var output = Render(partition, [("expression", m), ("mode", "import")]);

        Assert.DoesNotContain("\x1b[38;2;", output);
        Assert.Contains("expression: " + m, output);
    }

    private static string Render(
        ModelObject obj,
        (string Key, object? Value)[]? properties = null,
        IReadOnlySet<string>? measureNames = null)
    {
        var dictionary = properties is { Length: > 0 }
            ? properties.ToDictionary(entry => entry.Key, entry => entry.Value)
            : new Dictionary<string, object?>
            {
                ["name"] = obj.Name,
                ["expression"] = obj.Expression ?? "",
                ["formatString"] = "\"$\"#,0",
            };
        var result = new GetModelResult(obj.Kind.ToString(), obj.Path, dictionary, obj, measureNames);

        return ConsoleCapture.Run(
            () => { GetRenderer.Render(result, "text"); return 0; },
            captureAnsiConsole: true,
            forceAnsi: true).Stdout;
    }
}
