using Tomix.Core.Dax;

namespace Tomix.Core.Tests;

public sealed class DaxRowContextsTests
{
    // Each row context innermost first, separated by " / ": its sources (Table or Table[Column]),
    // "+name" for a column the expression builds, "?" for unknown rows. "" is outside every iterator.
    [Theory]
    [InlineData("SUM([Amount])", "[Amount]", "")]
    [InlineData("SUMX(Sales, [Amount])", "[Amount]", "Sales")]
    [InlineData("SUMX('Sales Header', [Amount])", "[Amount]", "Sales Header")]
    [InlineData("SUMX(Sales, SUMX(Customers, [Amount]))", "[Amount]", "Customers / Sales")]
    // The table argument itself is outside the iterator's rows.
    [InlineData("SUMX(FILTER(Sales, [Amount] > 0), 1)", "[Amount]", "Sales")]
    [InlineData("SUMX(Customers, COUNTROWS(FILTER(Sales, [Amount] > 0)))", "[Amount]", "Sales / Customers")]
    // Pass-through tables keep their rows' columns.
    [InlineData("SUMX(CALCULATETABLE(Sales, Sales[Year] = 2020), [Amount])", "[Amount]", "Sales")]
    [InlineData("MAXX(TOPN(3, Sales, 1), [Amount])", "[Amount]", "Sales")]
    [InlineData("SUMX(RELATEDTABLE(Sales), [Amount])", "[Amount]", "Sales")]
    [InlineData("VAR t = FILTER(Sales, TRUE()) RETURN SUMX(t, [Amount])", "[Amount]", "Sales")]
    // Columns keep only themselves.
    [InlineData("SUMX(VALUES(Sales[Year]), [Year])", "[Year]", "Sales[Year]")]
    [InlineData("SUMX(ALL(Sales[Year], Sales[Month]), [Year])", "[Year]", "Sales[Year],Sales[Month]")]
    [InlineData("SUMX(SUMMARIZE(Sales, Sales[Year], \"@T\", 1), [@T])", "[@T]", "Sales[Year]+@T")]
    [InlineData("SUMX(ADDCOLUMNS(Sales, \"@A\", 1), [@A])", "[@A]", "Sales+@A")]
    [InlineData("SUMX(SELECTCOLUMNS(Sales, \"@A\", 1), [@A])", "[@A]", "+@A")]
    [InlineData("SUMX(CROSSJOIN(VALUES(Sales[Year]), Customers), [Year])", "[Year]", "Sales[Year],Customers")]
    [InlineData("SUMX(GENERATESERIES(1, 3), [Value])", "[Value]", "+Value")]
    // Window functions: ORDERBY/PARTITIONBY/MATCHBY columns come from the relation.
    [InlineData("OFFSET(-1, ALL(Dates[Year]), ORDERBY([Year]))", "[Year]", "Dates[Year]")]
    [InlineData("WINDOW(1, ABS, 3, ABS, Dates, ORDERBY([Year]), , PARTITIONBY([Month]))", "[Month]", "Dates")]
    [InlineData("ROWNUMBER(Dates, ORDERBY([Year] * 2, DESC))", "[Year]", "Dates")]
    [InlineData("OFFSET(-1, ORDERBY([Year]))", "[Year]", "?")]
    [InlineData("SUMX(Sales, RANK(DENSE, ALL(Dates[Year]), ORDERBY([Year])))", "[Year]", "Dates[Year] / Sales")]
    // EARLIER reads an outer row context; which one is not tracked.
    [InlineData("SUMX(Sales, EARLIER([Amount]))", "[Amount]", "? / Sales")]
    // No rule for the table expression.
    [InlineData("SUMX(DATESYTD(Dates[Date]), [Amount])", "[Amount]", "?")]
    [InlineData("SUMX(SELECTCOLUMNS(Sales, Sales[Amount]), [Amount])", "[Amount]", "?")]
    // The parser reads a role filter as a "[Name] = expr" definition; it is still a reference.
    [InlineData("[Region] = \"EU\"", "[Region]", "")]
    public void Analyze_ReportsTheRowContextsAroundTheLastReference(string dax, string reference, string expected)
    {
        var contexts = DaxRowContexts.Analyze(dax);

        Assert.True(contexts.TryGetValue(dax.LastIndexOf(reference, StringComparison.Ordinal), out var stack));
        Assert.Equal(expected, string.Join(" / ", stack!.Select(Describe)));
    }

    private static string Describe(DaxRowContext context)
        => context.Unknown
            ? "?"
            : string.Join(",", context.Sources.Select(s => s.Column is null ? s.Table : $"{s.Table}[{s.Column}]"))
              + string.Concat(context.Built.Select(b => "+" + b));
}
