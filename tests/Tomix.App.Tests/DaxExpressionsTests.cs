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
    [InlineData("rlsExpression", true)]
    [InlineData("description", false)]
    [InlineData("formatString", false)]
    [InlineData("filterExpression", false)] // table permissions expose no DaxSite yet
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
