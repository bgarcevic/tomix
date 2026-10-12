using Tomix.App.Dax;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

/// <summary>
/// The DAX-vs-M decision shared by output renderers: which kinds' main Expression carries DAX
/// (the flattened kind/detail form of <see cref="DaxExpressions.Sites"/>), and the exact-text
/// match the get property view uses.
/// </summary>
public sealed class DaxExpressionsTests
{
    [Theory]
    [InlineData(ModelObjectKind.Measure, null, true)]
    [InlineData(ModelObjectKind.Column, null, true)]
    [InlineData(ModelObjectKind.CalculatedColumn, null, true)]
    [InlineData(ModelObjectKind.CalculationItem, null, true)]
    [InlineData(ModelObjectKind.Function, null, true)]
    [InlineData(ModelObjectKind.TablePermission, "read", true)]
    [InlineData(ModelObjectKind.Partition, "calculated", true)]
    [InlineData(ModelObjectKind.Partition, "Calculated", true)]
    [InlineData(ModelObjectKind.Partition, "import", false)]
    [InlineData(ModelObjectKind.Expression, null, false)] // shared expressions are M
    [InlineData(ModelObjectKind.Role, null, false)]
    [InlineData(ModelObjectKind.Table, null, false)]
    public void IsDaxExpression_MatchesSitesContract(ModelObjectKind kind, string? detail, bool expected)
        => Assert.Equal(expected, DaxExpressions.IsDaxExpression(kind, detail));

    [Theory]
    [InlineData("expression", true)]
    [InlineData("  Expression  ", true)]
    [InlineData("detailRowsExpression", true)]
    [InlineData("formatStringExpression", true)]
    [InlineData("defaultDetailRowsExpression", true)]
    [InlineData("noSelectionExpression", true)]
    [InlineData("multipleOrEmptySelectionExpression", true)]
    [InlineData("filterExpression", true)]
    [InlineData("rlsExpression", false)] // the role's "Table: filter" summary is not DAX
    [InlineData("description", false)]
    [InlineData("formatString", false)]
    public void MayBeDaxProperty_MatchesTheSiteKeys(string property, bool expected)
        => Assert.Equal(expected, DaxExpressions.MayBeDaxProperty(property));

    [Fact]
    public void Sites_YieldsCalculatedColumnExpression()
    {
        var calculated = new ModelObject(
            "Sorting", ModelObjectKind.CalculatedColumn, "Product/Sorting",
            Detail: null, Expression: "RELATED('Category'[Sorting])", Description: null,
            Hidden: false, SourceColumn: null, Children: []);

        var site = Assert.Single(DaxExpressions.Sites(calculated));
        Assert.Equal("Expression", site.Property);
        Assert.Equal("RELATED('Category'[Sorting])", site.Expression);
    }

    /// <summary>Every DAX-bearing property TOM has, by the kind and key the snapshot carries it under.</summary>
    [Theory]
    [InlineData(ModelObjectKind.Measure, "DetailRowsExpression")]
    [InlineData(ModelObjectKind.Measure, "FormatStringExpression")]
    [InlineData(ModelObjectKind.Measure, "KpiTargetExpression")]
    [InlineData(ModelObjectKind.Measure, "KpiStatusExpression")]
    [InlineData(ModelObjectKind.Measure, "KpiTrendExpression")]
    [InlineData(ModelObjectKind.Table, "DefaultDetailRowsExpression")]
    [InlineData(ModelObjectKind.Table, "NoSelectionExpression")]
    [InlineData(ModelObjectKind.Table, "MultipleOrEmptySelectionExpression")]
    [InlineData(ModelObjectKind.CalculationItem, "FormatStringExpression")]
    public void Sites_YieldsPropertyBagExpression(ModelObjectKind kind, string property)
    {
        var obj = new ModelObject(
            "X", kind, "T/X",
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null, Children: [],
            Properties: new Dictionary<string, string> { [property] = "[Amount]" });

        var site = Assert.Single(DaxExpressions.Sites(obj));
        Assert.Equal(property, site.Property);
        Assert.Equal("[Amount]", site.Expression);
    }

    [Fact]
    public void Sites_YieldsTablePermissionFilter_AndNothingForItsRole()
    {
        var permission = new ModelObject(
            "Sales", ModelObjectKind.TablePermission, "Roles/Reader/Sales",
            Detail: "default", Expression: "Sales[Region] = \"EU\"", Description: null,
            Hidden: false, SourceColumn: null, Children: []);
        var role = new ModelObject(
            "Reader", ModelObjectKind.Role, "Roles/Reader",
            Detail: "Read", Expression: null, Description: null, Hidden: false, SourceColumn: null,
            Children: [permission],
            Properties: new Dictionary<string, string> { ["RlsExpression"] = "Sales: Sales[Region] = \"EU\"" });

        var site = Assert.Single(DaxExpressions.Sites(permission));
        Assert.Equal("FilterExpression", site.Property);
        Assert.Equal("Sales[Region] = \"EU\"", site.Expression);
        Assert.Empty(DaxExpressions.Sites(role));
    }

    [Fact]
    public void IsDaxValue_MatchesByExactText()
    {
        var measure = new ModelObject(
            "Sales", ModelObjectKind.Measure, "Sales",
            Detail: null, Expression: "SUM('Sales'[Amount])", Description: null,
            Hidden: false, SourceColumn: null, Children: []);

        Assert.True(DaxExpressions.IsDaxValue(measure, "SUM('Sales'[Amount])"));
        Assert.False(DaxExpressions.IsDaxValue(measure, "SUM('Sales'[Qty])"));
        Assert.False(DaxExpressions.IsDaxValue(measure, "sum('sales'[amount])"));
    }
}
