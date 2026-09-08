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
