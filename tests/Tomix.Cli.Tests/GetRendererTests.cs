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
    private const string Function = "\x1b[38;5;12m";  // functions (bright blue)
    private const string Keyword = "\x1b[38;5;5m";   // keywords (magenta)
    private const string Reference = "\x1b[38;5;6m"; // measure references (cyan)

    [Fact]
    public void DaxExpression_IsHighlighted()
    {
        var measure = new ModelObject("Cost", ModelObjectKind.Measure, "Sales/Cost",
            Detail: null, Expression: "SUM('Sales'[Total Product Cost])", Description: null,
            Hidden: false, SourceColumn: null, Children: []);

        var output = Render(measure);

        Assert.Contains(Function + "SUM", output);
        // Tables and columns stay plain.
        Assert.Contains("('Sales'[Total Product Cost])", output);
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

        Assert.Contains(Reference + "[Profit]", output);
        Assert.Contains(", 'Sales'[Qty])", output);
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

        Assert.Contains(Function + "Table.SelectRows", output);
        Assert.Contains(Keyword + "each", output);
        Assert.Contains(" [X] > ", output);
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

        Assert.Contains(Keyword + "meta", output);
        Assert.DoesNotContain(Reference, output);
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
        var result = new GetObjectResult(obj.Kind.ToString(), obj.Path, dictionary, obj, measureNames);

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

    /// <summary>
    /// A fragment is the provider serializer's text (GetObjectResult.Script), printed as is: no
    /// markup, no envelope, one trailing newline, LF line endings on every platform.
    /// </summary>
    [Theory]
    [InlineData(OutputFormats.Tmdl)]
    [InlineData(OutputFormats.Bim)]
    [InlineData(OutputFormats.Tmsl)]
    public void Script_IsPrintedVerbatim(string format)
    {
        const string script = "ref table Sales\r\n\r\n\tmeasure '[Cost]' = 1\r\n\t\tformatString: #,0\r\n\r\n";
        var measure = Cost("1");
        var result = new GetObjectResult("Measure", measure.Path, ModelPropertyCatalog.Project(measure), measure, Script: script);

        var output = ConsoleCapture.Run(() => { GetRenderer.Render(result, format); return 0; }).Stdout;

        Assert.Equal("ref table Sales\n\n\tmeasure '[Cost]' = 1\n\t\tformatString: #,0" + Environment.NewLine, output);
    }
}
