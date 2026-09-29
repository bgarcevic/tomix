using Tomix.Core.Dax;

namespace Tomix.Core.Tests;

public sealed class DaxQueryColumnsTests
{
    [Theory]
    // Iterators put their table's columns in scope for the row-context arguments.
    [InlineData("FILTER(ADDCOLUMNS(Sales, \"@A\", 1), [@A] > 0)", "[@A]", DaxQueryColumnScope.InScope)]
    [InlineData("SUMX(SELECTCOLUMNS(Sales, \"@A\", 1), [@A])", "[@A]", DaxQueryColumnScope.InScope)]
    [InlineData("SUMX(SUMMARIZE(Sales, Sales[Amount], \"@T\", 1), [@T])", "[@T]", DaxQueryColumnScope.InScope)]
    [InlineData("SUMX(SUMMARIZECOLUMNS(Sales[Amount], \"@T\", 1), [@T])", "[@T]", DaxQueryColumnScope.InScope)]
    [InlineData("SUMX(ROW(\"@One\", 1), [@One])", "[@One]", DaxQueryColumnScope.InScope)]
    [InlineData("MAXX(TOPN(3, ADDCOLUMNS(Sales, \"@A\", 1), [@A]), [@A])", "[@A]", DaxQueryColumnScope.InScope)]
    // Pass-through tables keep the columns: FILTER over ADDCOLUMNS, then SUMX.
    [InlineData("SUMX(FILTER(ADDCOLUMNS(Sales, \"@A\", 1), TRUE()), [@A])", "[@A]", DaxQueryColumnScope.InScope)]
    // Nested row contexts stay visible.
    [InlineData("SUMX(ADDCOLUMNS(Sales, \"@A\", 1), SUMX(Customer, [@A]))", "[@A]", DaxQueryColumnScope.InScope)]
    // A VAR holding a table carries its columns to where it is iterated.
    [InlineData("VAR T = ADDCOLUMNS(Sales, \"@A\", 1) RETURN SUMX(T, [@A])", "[@A]", DaxQueryColumnScope.InScope)]
    [InlineData("VAR T = ADDCOLUMNS(Sales, \"@A\", 1) VAR U = FILTER(T, [@A] > 0) RETURN COUNTROWS(U)", "[@A]", DaxQueryColumnScope.InScope)]
    // [Value] from GENERATESERIES and table constructors.
    [InlineData("SUMX(GENERATESERIES(1, 12), [Value])", "[Value]", DaxQueryColumnScope.InScope)]
    [InlineData("SUMX({ 1, 2 }, [Value])", "[Value]", DaxQueryColumnScope.InScope)]
    [InlineData("SUMX({ (1, 2) }, [Value2])", "[Value2]", DaxQueryColumnScope.InScope)]
    // Used outside the table that has it.
    [InlineData("COUNTROWS(FILTER(ADDCOLUMNS(Sales, \"@A\", 1), TRUE())) + [@A]", "[@A]", DaxQueryColumnScope.OutOfScope)]
    [InlineData("VAR T = ADDCOLUMNS(Sales, \"@A\", 1) RETURN [@A]", "[@A]", DaxQueryColumnScope.OutOfScope)]
    [InlineData("SUMX(ADDCOLUMNS(Sales, \"@A\", [@A]), 1)", "[@A]", DaxQueryColumnScope.OutOfScope)]
    [InlineData("SUMX(Sales, [Value]) + SUMX(GENERATESERIES(1, 2), 1)", "[Value]", DaxQueryColumnScope.OutOfScope)]
    // SELECTCOLUMNS keeps only what it lists.
    [InlineData("SUMX(SELECTCOLUMNS(ADDCOLUMNS(Sales, \"@A\", 1), \"@B\", 2), [@A])", "[@A]", DaxQueryColumnScope.OutOfScope)]
    // Not built by the expression: left to the model lookup.
    [InlineData("SUMX(Sales, [Amount])", "[Amount]", DaxQueryColumnScope.NotDefined)]
    [InlineData("IF([Status] = \"Status\", 1)", "[Status]", DaxQueryColumnScope.NotDefined)]
    [InlineData("IF(1 IN { 1, 2 }, [Value])", "[Value]", DaxQueryColumnScope.NotDefined)]
    [InlineData("SUMX(DATATABLE(\"N\", INTEGER, {{1}}), [Value])", "[Value]", DaxQueryColumnScope.NotDefined)]
    [InlineData("SUMX(ADDCOLUMNS(Sales, \"@Kind\", \"@Other\"), [@Other])", "[@Other]", DaxQueryColumnScope.NotDefined)]
    [InlineData("SUMX(Sales, [@A]) // \"@A\"", "[@A]", DaxQueryColumnScope.NotDefined)]
    public void Analyze_ClassifiesTheLastReference(string dax, string reference, DaxQueryColumnScope expected)
    {
        var scopes = DaxQueryColumns.Analyze(dax);

        var start = dax.LastIndexOf(reference, StringComparison.Ordinal);
        Assert.Equal(expected, scopes.GetValueOrDefault(start, DaxQueryColumnScope.NotDefined));
    }

    [Theory]
    [InlineData("DATATABLE(\"Group\", STRING, \"Offset\", INTEGER, {{\"a, b\", 1}})", "Group,Offset")]
    [InlineData("-- axis\nROW(\"Group\", \"x\", \"Offset\", 1)", "Group,Offset")]
    [InlineData("GENERATESERIES(1, 10)", "Value")]
    [InlineData("{ (1, \"a\") }", "Value1,Value2")]
    [InlineData("SELECTCOLUMNS(Sales, \"Group\", Sales[Amount])", null)]
    [InlineData("FILTER(DATATABLE(\"Group\", STRING, {{\"a\"}}), TRUE())", null)]
    [InlineData("ROW(\"Group\", 1, Name, 2)", null)]
    public void CalculatedTableColumns_OnlyWhenSpelledOut(string dax, string? expected)
    {
        var columns = DaxQueryColumns.CalculatedTableColumns(dax);

        Assert.Equal(expected, columns is null ? null : string.Join(",", columns.Order(StringComparer.Ordinal)));
    }
}
