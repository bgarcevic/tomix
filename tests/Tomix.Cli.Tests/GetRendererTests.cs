using System.Text.Json;
using Tomix.App.Get;
using Tomix.Cli.Output;
using Tomix.Core.Models;
using Tomix.Core.Properties;

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
    private const string Lav = "\x1b[38;2;133;114;175m";    // keywords

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
    public void MPartitionExpression_IsHighlighted()
    {
        const string m = "Table.SelectRows(Source, each [X] > 0)";
        var partition = new ModelObject("Part", ModelObjectKind.Partition, "Sales/Part",
            Detail: "import", Expression: m, Description: null, Hidden: false,
            SourceColumn: null, Children: [],
            Properties: new Dictionary<string, string> { ["PartitionSourceType"] = "M" });

        var output = Render(partition, [("expression", m), ("mode", "import")]);

        Assert.Contains(Harbor + "Table.SelectRows", output);
        Assert.Contains(Lav + "each", output);
        Assert.Contains(Moss + "[X]", output);
        // Only the expression highlights; other values (the mode) stay plain.
        var visible = StripAnsi(output);
        Assert.Contains("mode        import", visible);
        Assert.Contains("expression  " + m, visible);
    }

    [Fact]
    public void SharedExpression_IsHighlightedAsM()
    {
        const string m = "\"dev\" meta [IsParameterQuery = true]";
        var expression = new ModelObject("Env", ModelObjectKind.Expression, "Expressions/Env",
            Detail: "M", Expression: m, Description: null, Hidden: false,
            SourceColumn: null, Children: []);

        var output = Render(expression, [("name", "Env"), ("expression", m)]);

        Assert.Contains(Lav + "meta", output);
        Assert.DoesNotContain(Sage, output);
    }

    private static string Render(
        ModelObject obj,
        (string Key, object? Value)[]? properties = null,
        IReadOnlySet<string>? measureNames = null,
        bool all = false)
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
            () => { GetRenderer.Render(result, "text", all); return 0; },
            captureAnsiConsole: true,
            forceAnsi: true).Stdout;
    }

    [Fact]
    public void DefaultView_FoldsUnsetSettableProperties_AndDropsReadOnlyDefaults()
    {
        var visible = StripAnsi(Render(Cost("SUM('Sales'[Cost])"), Catalog(Cost("SUM('Sales'[Cost])"))));

        Assert.StartsWith("Sales/Cost  Measure", visible);
        Assert.Contains("expression  SUM('Sales'[Cost])", visible);
        // The title names the object, so the redundant name row is gone.
        Assert.DoesNotContain("name ", visible);
        // Writable defaults fold into the Not set line; read-only defaults (kpi) are dropped.
        Assert.Contains("Not set: description, isHidden, formatString", visible);
        Assert.DoesNotContain("kpi", visible);
        Assert.DoesNotContain("False", visible);
    }

    [Fact]
    public void AllView_ListsEveryProperty_WithoutNotSetLine()
    {
        var measure = Cost("SUM('Sales'[Cost])");

        var visible = StripAnsi(Render(measure, Catalog(measure), all: true));

        // name and expression are the only non-default values of the 17 measure properties.
        Assert.StartsWith("Sales/Cost  Measure  · 2 of 17 set", visible);
        var lines = visible.ReplaceLineEndings("\n").Split('\n');
        // Settable rows carry no tag.
        Assert.Contains("  name                    Cost", lines);
        Assert.Contains("  isHidden                False", lines);
        Assert.Contains("kpiTargetExpression     —  read-only", visible);
        Assert.DoesNotContain("Not set", visible);
    }

    [Fact]
    public void AnnotationsAndTranslations_RenderInTheirOwnSections()
    {
        var measure = Cost("1") with
        {
            Properties = new Dictionary<string, string>
            {
                [PropertyBagKeys.AnnotationPrefix + "PBI_FormatHint"] = "{}",
                [PropertyBagKeys.TranslationPrefix + "da-DK/Caption"] = "Omkostning",
            }
        };

        var visible = StripAnsi(Render(measure, Catalog(measure))).ReplaceLineEndings("\n");

        Assert.Contains("  Annotations\n    PBI_FormatHint  {}", visible);
        Assert.Contains("  Translations\n    da-DK/caption  Omkostning", visible);
    }

    [Fact]
    public void MultiLineExpression_RendersAsIndentedBlockUnderItsKey()
    {
        var measure = Cost("VAR x = 1\r\nRETURN\r\n    x");

        var visible = StripAnsi(Render(measure, Catalog(measure))).ReplaceLineEndings("\n");

        Assert.Contains("\n  expression\n    VAR x = 1\n    RETURN\n        x\n", visible);
    }

    [Fact]
    public void RelationshipName_IsKeptWhenItDiffersFromThePath()
    {
        var relationship = new ModelObject("Sales[Key] -> Customer[Key]", ModelObjectKind.Relationship,
            "Relationships/0a1b", Detail: null, Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: [],
            Properties: new Dictionary<string, string>
            {
                [PropertyBagKeys.IsActive] = "true",
                [PropertyBagKeys.CrossFilteringBehavior] = "OneDirection",
            });

        var visible = StripAnsi(Render(relationship, Catalog(relationship)));

        Assert.Contains("name  Sales[Key] -> Customer[Key]", visible);
        // Declared engine defaults fold like empty values.
        Assert.Contains("Not set: fromCardinality, toCardinality, crossFilteringBehavior, isActive,", visible);
    }

    private static ModelObject Cost(string expression)
        => new("Cost", ModelObjectKind.Measure, "Sales/Cost", Detail: null, Expression: expression,
            Description: null, Hidden: false, SourceColumn: null, Children: []);

    private static (string Key, object? Value)[] Catalog(ModelObject obj)
        => ModelPropertyCatalog.Project(obj).Select(p => (p.Key, p.Value)).ToArray();

    private static string StripAnsi(string text)
        => System.Text.RegularExpressions.Regex.Replace(text, "\x1b\\[[0-9;]*m", "");

    [Fact]
    public void CalculatedColumn_Tmdl_ShowsInlineExpression()
    {
        var output = RenderFragment(CalculatedColumn(), OutputFormats.Tmdl);

        Assert.Contains("ref table Product", output);
        Assert.Contains("\tcolumn Sorting = RELATED('Category'[Sorting])", output);
        Assert.DoesNotContain("sourceColumn", output);
        // Not Assert.DoesNotContain: xUnit strips control characters from the needle, so a bare
        // ESC matches everything. An ordinal IndexOf is the honest markup-freedom check.
        Assert.Equal(-1, output.IndexOf(((char)27).ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void CalculatedColumn_Bim_ShowsExpressionFragment()
    {
        var output = RenderFragment(CalculatedColumn(), OutputFormats.Bim);

        var fragment = JsonDocument.Parse(output).RootElement;
        Assert.Equal("Sorting", fragment.GetProperty("name").GetString());
        Assert.Equal("RELATED('Category'[Sorting])", fragment.GetProperty("expression").GetString());
        Assert.False(fragment.TryGetProperty("sourceColumn", out _));
        Assert.Equal(-1, output.IndexOf(((char)27).ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void TableTmdl_IncludesCalculatedColumns()
    {
        var output = RenderFragment(ProductTable(), OutputFormats.Tmdl);

        Assert.Contains("\tcolumn Amount", output);
        Assert.Contains("\t\tsourceColumn: Amount", output);
        Assert.Contains("\tcolumn Sorting = RELATED('Category'[Sorting])", output);
    }

    [Fact]
    public void TableBim_IncludesCalculatedColumns()
    {
        var output = RenderFragment(ProductTable(), OutputFormats.Bim);

        var columns = JsonDocument.Parse(output).RootElement.GetProperty("columns");
        Assert.Equal(2, columns.GetArrayLength());
        var amount = columns.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "Amount");
        Assert.False(amount.TryGetProperty("expression", out _));
        var sorting = columns.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "Sorting");
        Assert.Equal("RELATED('Category'[Sorting])", sorting.GetProperty("expression").GetString());
        Assert.False(sorting.TryGetProperty("sourceColumn", out _));
    }

    private static ModelObject CalculatedColumn()
        => new("Sorting", ModelObjectKind.CalculatedColumn, "Product/Sorting",
            Detail: "Int64", Expression: "RELATED('Category'[Sorting])", Description: null,
            Hidden: false, SourceColumn: null, Children: []);

    private static ModelObject ProductTable()
        => new("Product", ModelObjectKind.Table, "Product",
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null,
            Children:
            [
                new ModelObject("Amount", ModelObjectKind.Column, "Product/Amount",
                    Detail: "Double", Expression: null, Description: null, Hidden: false,
                    SourceColumn: "Amount", Children: []),
                CalculatedColumn(),
            ]);

    /// <summary>Fragment formats receive the raw object with no property dictionary — the
    /// renderer decides the shape from the kind alone.</summary>
    private static string RenderFragment(ModelObject obj, string format)
    {
        var result = new GetModelResult(obj.Kind.ToString(), obj.Path, new Dictionary<string, object?>(), obj, null);

        return ConsoleCapture.Run(
            () => { GetRenderer.Render(result, format); return 0; }).Stdout;
    }
}
