using Tomix.Core.Dax;

namespace Tomix.Core.Tests;

public sealed class DaxLanguageTests
{
    [Fact]
    public void Classify_MeasureExpression_ClassifiesEveryRole()
    {
        const string dax = "VAR Total = SUM(Sales[Amount]) RETURN Total + 0.5";

        Assert.Equal(DaxTextClassification.Keyword, ClassificationOf(dax, "VAR"));
        Assert.Equal(DaxTextClassification.Variable, ClassificationOf(dax, "Total ="));
        Assert.Equal(DaxTextClassification.Function, ClassificationOf(dax, "SUM"));
        Assert.Equal(DaxTextClassification.TableName, ClassificationOf(dax, "Sales"));
        Assert.Equal(DaxTextClassification.ColumnReference, ClassificationOf(dax, "[Amount]"));
        Assert.Equal(DaxTextClassification.Keyword, ClassificationOf(dax, "RETURN"));
        // The variable is recognized at every use, not just where it is declared.
        Assert.Equal(DaxTextClassification.Variable, ClassificationOf(dax, "Total +"));
        Assert.Equal(DaxTextClassification.Number, ClassificationOf(dax, "0.5"));
    }

    [Fact]
    public void Classify_StringsAndComments_ContainMisleadingCharacters()
    {
        const string dax = "COUNTROWS('Order Lines') & \"x // y\" // real comment";

        Assert.Equal(DaxTextClassification.TableName, ClassificationOf(dax, "'Order Lines'"));
        // A "//" inside a string is string, not comment.
        Assert.Equal(DaxTextClassification.StringLiteral, ClassificationOf(dax, "\"x // y\""));
        Assert.Equal(DaxTextClassification.Comment, ClassificationOf(dax, "// real comment"));
    }

    [Fact]
    public void Classify_UnknownCallName_IsNotAFunction()
    {
        const string dax = "SomeFunc(Sales[Amount])";

        Assert.Equal(DaxTextClassification.Text, ClassificationOf(dax, "SomeFunc"));
    }

    [Fact]
    public void Classify_FilterComparison_IsNotMistakenForADefinition()
    {
        const string dax = "CALCULATE([Sales], 'Date Role'[Date Role] = \"By Due Date\")";

        // The equality inside the filter must not turn the expression head into a definition name:
        // the same CALCULATE has to color the same way with and without a filter argument.
        Assert.DoesNotContain(DaxLanguage.Classify(dax), span => span.Classification == DaxTextClassification.DefinitionName);
        Assert.Equal(DaxTextClassification.Function, ClassificationOf(dax, "CALCULATE"));
        Assert.Equal(DaxTextClassification.ColumnReference, ClassificationOf(dax, "[Sales]"));
        Assert.Equal(DaxTextClassification.TableName, ClassificationOf(dax, "'Date Role'"));
        Assert.Equal(DaxTextClassification.ColumnReference, ClassificationOf(dax, "[Date Role]"));
        Assert.Equal(DaxTextClassification.StringLiteral, ClassificationOf(dax, "\"By Due Date\""));
    }

    [Fact]
    public void Classify_ComparisonExpression_IsNotMistakenForADefinition()
    {
        const string dax = "[Sales] - [Cost] = 0";

        Assert.DoesNotContain(DaxLanguage.Classify(dax), span => span.Classification == DaxTextClassification.DefinitionName);
        Assert.Equal(DaxTextClassification.ColumnReference, ClassificationOf(dax, "[Sales]"));
        Assert.Equal(DaxTextClassification.ColumnReference, ClassificationOf(dax, "[Cost]"));
    }

    [Fact]
    public void Classify_BareDefinition_StillMarksTheName()
    {
        const string dax = "Sales Amount % = SUM('Sales'[Amount])";

        Assert.Equal(DaxTextClassification.DefinitionName, ClassificationOf(dax, "Sales"));
        Assert.Equal(DaxTextClassification.Function, ClassificationOf(dax, "SUM"));
        Assert.Equal(DaxTextClassification.TableName, ClassificationOf(dax, "'Sales'"));
    }

    [Fact]
    public void Classify_BracketedDefinitionWithParens_StillMarksTheName()
    {
        const string dax = "[Order Quantity (base)] = SUM('Sales'[Amount])";

        // The parentheses live inside the bracketed name token, so the definition survives.
        var name = SpanContaining(dax, "[Order Quantity (base)]");
        Assert.Equal(DaxTextClassification.DefinitionName, name.Classification);
        Assert.Equal("[Order Quantity (base)]".Length, name.Length);
        Assert.Equal(DaxTextClassification.Function, ClassificationOf(dax, "SUM"));
    }

    [Fact]
    public void Classify_DottedFunctionAndDateLiteralAndParameter()
    {
        Assert.Equal(DaxTextClassification.Function, ClassificationOf("NORM.DIST(1, 0, 1, TRUE)", "NORM.DIST"));
        Assert.Equal(DaxTextClassification.StringLiteral, ClassificationOf("DATE(1, 1, dt\"2024-01-01\")", "dt\"2024-01-01\""));
        Assert.Equal(DaxTextClassification.QueryParameter, ClassificationOf("@Risk", "@Risk"));
    }

    [Fact]
    public void Classify_ArgumentWord_IsKeyword_BareWord_IsTable()
    {
        const string dax = "TOPN(1, Sales, Sales[Amount], DESC)";

        Assert.Equal(DaxTextClassification.Keyword, ClassificationOf(dax, "DESC"));
        Assert.Equal(DaxTextClassification.TableName, ClassificationOf(dax, "Sales,"));
    }

    [Fact]
    public void Classify_DoubledEscapes_AreOneSpan()
    {
        const string dax = "[Weird ]] Name] & 'It''s'[OK]";

        var column = SpanContaining(dax, "[Weird ]] Name]");
        Assert.Equal(DaxTextClassification.ColumnReference, column.Classification);
        Assert.Equal("[Weird ]] Name]".Length, column.Length);

        var table = SpanContaining(dax, "'It''s'");
        Assert.Equal(DaxTextClassification.TableName, table.Classification);
        Assert.Equal("'It''s'".Length, table.Length);
    }

    [Fact]
    public void Classify_SpansAreOrderedAndNeverOverlap()
    {
        const string dax = "VAR x = CALCULATE(SUM(Sales[Amount]), Sales[Date] > dt\"2024-01-01\") // done\nRETURN x";

        var spans = DaxLanguage.Classify(dax);

        Assert.True(spans.Count > 0);
        for (var index = 1; index < spans.Count; index++)
        {
            var previous = spans[index - 1];
            Assert.True(previous.Start + previous.Length <= spans[index].Start,
                $"Span {index - 1} [{previous.Start}..{previous.Start + previous.Length}) overlaps span {index} [{spans[index].Start}..{spans[index].Start + spans[index].Length})");
            Assert.True(previous.Start < spans[index].Start);
        }
    }

    [Fact]
    public void Classify_MeasureNames_ResolveBracketedReferences()
    {
        // [Profit] is unqualified, 'Sales'[Sales Amount] qualified, and [Total Product Cost] a
        // plain column — resolution must not depend on the reference's shape.
        const string dax = "CALCULATE([Profit], 'Sales'[Sales Amount]) - [Total Product Cost]";
        var measures = Names("Profit", "Sales Amount");

        Assert.Equal(DaxTextClassification.MeasureReference, ClassificationWith(measures, dax, "[Profit]"));
        Assert.Equal(DaxTextClassification.MeasureReference, ClassificationWith(measures, dax, "[Sales Amount]"));
        Assert.Equal(DaxTextClassification.ColumnReference, ClassificationWith(measures, dax, "[Total Product Cost]"));
    }

    [Fact]
    public void Classify_MeasureResolution_IsCaseInsensitive()
    {
        const string dax = "[profit] + [Margin]";

        Assert.Equal(
            DaxTextClassification.MeasureReference,
            ClassificationWith(Names("Profit"), dax, "[profit]"));
        Assert.Equal(
            DaxTextClassification.ColumnReference,
            ClassificationWith(Names("Profit"), dax, "[Margin]"));
    }

    [Fact]
    public void Classify_WithoutMeasureNames_BracketsStayColumns()
    {
        const string dax = "[Profit] - [Cost]";

        Assert.DoesNotContain(
            DaxLanguage.Classify(dax, null),
            span => span.Classification == DaxTextClassification.MeasureReference);
    }

    private static IReadOnlySet<string> Names(params string[] names)
        => new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    private static DaxTextClassification ClassificationWith(IReadOnlySet<string> measures, string dax, string marker)
        => SpanContainingWith(measures, dax, marker).Classification;

    private static DaxClassifiedSpan SpanContainingWith(IReadOnlySet<string> measures, string dax, string text)
    {
        var start = dax.IndexOf(text, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Text '{text}' not found in '{dax}'.");

        var spans = DaxLanguage.Classify(dax, measures);
        return Assert.Single(spans, span => span.Start <= start && start < span.Start + span.Length);
    }

    private static DaxTextClassification ClassificationOf(string dax, string marker)
        => SpanContaining(dax, marker.TrimEnd(',')).Classification;

    private static DaxClassifiedSpan SpanContaining(string dax, string text)
    {
        var start = dax.IndexOf(text, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Text '{text}' not found in '{dax}'.");

        var spans = DaxLanguage.Classify(dax);
        return Assert.Single(spans, span => span.Start <= start && start < span.Start + span.Length);
    }
}
