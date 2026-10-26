using Tomix.Core.Dax;

namespace Tomix.Core.Tests;

public sealed class DaxPatternsTests
{
    [Theory]
    [InlineData("VAR a = 1 VAR b = 2 RETURN a", new[] { "b" })]
    [InlineData("VAR a = 1 VAR b = a + 1 RETURN b", new string[0])] // read by a later variable
    [InlineData("VAR t = FILTER(Sales, Sales[Qty] > 1) RETURN COUNTROWS(t)", new string[0])]
    [InlineData("VAR t = Sales RETURN SUMX(t, t[Amount])", new string[0])] // read as a table qualifier
    [InlineData("VAR Total = 1 RETURN total", new string[0])] // names are case-insensitive
    [InlineData("VAR a = 1 RETURN \"a\" & [a] // a", new[] { "a" })] // a string, a column and a comment are not reads
    [InlineData("VAR x = 1 RETURN SUMX(Sales, VAR y = 2 RETURN Sales[Qty])", new[] { "x", "y" })]
    [InlineData("SUM(Sales[Amount])", new string[0])]
    public void UnusedVariables_ReportsVariablesNothingReads(string dax, string[] expected)
        => Assert.Equal(expected, DaxPatterns.UnusedVariables(dax));

    [Theory]
    [InlineData("VAR a = 1 RETURN")] // syntax error: validate reports it, not the linter
    [InlineData("")]
    [InlineData(null)]
    public void UnusedVariables_BrokenOrEmpty_ReportsNothing(string? dax)
        => Assert.Empty(DaxPatterns.UnusedVariables(dax));

    [Theory]
    [InlineData("CALCULATE([Sales])", 1)]
    [InlineData("CALCULATE ( ( 'Sales'[Total] ) )", 1)]
    [InlineData("SUMX(Customer, CALCULATE([Sales]))", 1)] // the measure reference transitions already
    [InlineData("CALCULATE(SUM(Sales[Amount]))", 1)] // no row context at the top level
    [InlineData("VAR x = CALCULATE(SUM(Sales[Amount])) RETURN x * 2", 1)]
    [InlineData("CALCULATETABLE(Sales)", 1)]
    [InlineData("SUMX(Customer, CALCULATE(SUM(Sales[Amount])))", 0)] // context transition is the point
    [InlineData("ADDCOLUMNS(Customer, \"S\", CALCULATE(SUM(Sales[Amount])))", 0)]
    [InlineData("CALCULATE([Sales], Sales[Qty] > 1)", 0)]
    [InlineData("\"CALCULATE([Sales])\" // CALCULATE([Sales])", 0)]
    [InlineData("CALCULATE([Sales]", 0)] // syntax error
    public void RedundantCalculates_CountsFilterlessCalls(string dax, int expected)
        => Assert.Equal(expected, DaxPatterns.RedundantCalculates(dax).Count);

    [Fact]
    public void RedundantCalculates_ReportsTheOffsetOfTheFunctionName()
    {
        const string dax = "1 + CALCULATE ( [Sales] )";

        Assert.Equal([dax.IndexOf("CALCULATE", StringComparison.Ordinal)], DaxPatterns.RedundantCalculates(dax));
    }
}
