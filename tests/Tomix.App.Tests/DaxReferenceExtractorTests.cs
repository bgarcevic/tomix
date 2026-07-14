using Tomix.App.Dax;

namespace Tomix.App.Tests;

/// <summary>
/// The extractor is lexer-based so references inside strings/comments are never reported, escaped
/// names round-trip, and every reference carries the exact character span a rename rewrite needs.
/// </summary>
public sealed class DaxReferenceExtractorTests
{
    [Fact]
    public void QuotedQualifiedReference()
    {
        var reference = Assert.Single(DaxReferenceExtractor.Extract("SUM('Order Lines'[Amount])"));

        Assert.Equal(DaxReferenceShape.Qualified, reference.Shape);
        Assert.Equal("Order Lines", reference.Table);
        Assert.Equal("Amount", reference.Object);
        AssertSpan("'Order Lines'[Amount]", "SUM('Order Lines'[Amount])", reference);
    }

    [Fact]
    public void BareQualifiedReference()
    {
        var reference = Assert.Single(DaxReferenceExtractor.Extract("SUM(Sales[Amount])"));

        Assert.Equal(DaxReferenceShape.Qualified, reference.Shape);
        Assert.Equal("Sales", reference.Table);
        AssertSpan("Sales[Amount]", "SUM(Sales[Amount])", reference);
    }

    [Fact]
    public void QualifiedReference_WhitespaceBetweenTableAndBracket()
    {
        var reference = Assert.Single(DaxReferenceExtractor.Extract("'Sales' [Amount]"));

        Assert.Equal(DaxReferenceShape.Qualified, reference.Shape);
        AssertSpan("'Sales' [Amount]", "'Sales' [Amount]", reference);
    }

    [Fact]
    public void UnqualifiedReference()
    {
        var reference = Assert.Single(DaxReferenceExtractor.Extract("[Base] * 2"));

        Assert.Equal(DaxReferenceShape.Unqualified, reference.Shape);
        Assert.Null(reference.Table);
        Assert.Equal("Base", reference.Object);
        AssertSpan("[Base]", "[Base] * 2", reference);
    }

    [Fact]
    public void QuotedTableReference()
    {
        var reference = Assert.Single(DaxReferenceExtractor.Extract("COUNTROWS('Udlån')"));

        Assert.Equal(DaxReferenceShape.Table, reference.Shape);
        Assert.Equal("Udlån", reference.Table);
        Assert.Null(reference.Object);
        AssertSpan("'Udlån'", "COUNTROWS('Udlån')", reference);
    }

    [Fact]
    public void BareTableCandidate()
    {
        var reference = Assert.Single(DaxReferenceExtractor.Extract("COUNTROWS(Region)"));

        Assert.Equal(DaxReferenceShape.TableCandidate, reference.Shape);
        Assert.Equal("Region", reference.Table);
    }

    [Fact]
    public void FunctionNames_AreNotTableCandidates()
    {
        // COUNTROWS is followed by '(' -> function, not a candidate.
        var references = DaxReferenceExtractor.Extract("COUNTROWS(Region)");

        Assert.Single(references);
    }

    [Fact]
    public void VarNames_AreNotTableCandidates()
    {
        var references = DaxReferenceExtractor.Extract(
            "VAR Sales = 1 RETURN Sales + [Amount]");

        var reference = Assert.Single(references);
        Assert.Equal(DaxReferenceShape.Unqualified, reference.Shape);
        Assert.Equal("Amount", reference.Object);
    }

    [Fact]
    public void Keywords_AreNotTableCandidates()
    {
        Assert.Empty(DaxReferenceExtractor.Extract("VAR x = TRUE RETURN NOT FALSE"));
    }

    [Fact]
    public void KeywordBeforeBracket_DoesNotQualifyTheReference()
    {
        // "RETURN [Total]" is the keyword followed by an unqualified measure — pairing it as
        // table "RETURN" would drop the dependency (no such table resolves).
        const string expression = "VAR x = 1 RETURN [Total Sales] + x";
        var reference = Assert.Single(DaxReferenceExtractor.Extract(expression));

        Assert.Equal(DaxReferenceShape.Unqualified, reference.Shape);
        Assert.Equal("Total Sales", reference.Object);
        AssertSpan("[Total Sales]", expression, reference);
    }

    [Fact]
    public void OperatorKeywordBeforeBracket_DoesNotQualifyTheReference()
    {
        var references = DaxReferenceExtractor.Extract("IF(NOT [IsActive], 0, [Amount])");

        Assert.Equal(["IsActive", "Amount"], references.Select(r => r.Object));
        Assert.All(references, r => Assert.Equal(DaxReferenceShape.Unqualified, r.Shape));
    }

    [Fact]
    public void VarNameBeforeBracket_DoesNotQualifyTheReference()
    {
        // DAX forbids a VAR sharing a table's name, so a VAR name can never qualify a bracket.
        var references = DaxReferenceExtractor.Extract("VAR x = 1 RETURN x [Total]");

        var reference = Assert.Single(references);
        Assert.Equal(DaxReferenceShape.Unqualified, reference.Shape);
        Assert.Equal("Total", reference.Object);
    }

    [Fact]
    public void BareQualifiedReference_WhitespaceBetweenTableAndBracket()
    {
        var reference = Assert.Single(DaxReferenceExtractor.Extract("SUM(Sales [Amount])"));

        Assert.Equal(DaxReferenceShape.Qualified, reference.Shape);
        Assert.Equal("Sales", reference.Table);
        AssertSpan("Sales [Amount]", "SUM(Sales [Amount])", reference);
    }

    [Fact]
    public void ReferencesInsideStringLiterals_AreIgnored()
    {
        var references = DaxReferenceExtractor.Extract(
            """IF([Flag], "see 'Sales'[Amount] for details", [Fallback])""");

        Assert.Equal(["Flag", "Fallback"], references.Select(r => r.Object));
    }

    [Fact]
    public void ReferencesInsideComments_AreIgnored()
    {
        var references = DaxReferenceExtractor.Extract(
            "// uses 'Sales'[Amount]\n-- and [Old]\n/* plus 'Region' */\n[Real]");

        var reference = Assert.Single(references);
        Assert.Equal("Real", reference.Object);
    }

    [Fact]
    public void EscapedNames_AreUnescaped()
    {
        var references = DaxReferenceExtractor.Extract("'It''s a table'[Col]]umn]");

        var reference = Assert.Single(references);
        Assert.Equal("It's a table", reference.Table);
        Assert.Equal("Col]umn", reference.Object);
        AssertSpan("'It''s a table'[Col]]umn]", "'It''s a table'[Col]]umn]", reference);
    }

    [Fact]
    public void StringWithEscapedQuotes_DoesNotSwallowFollowingReference()
    {
        var references = DaxReferenceExtractor.Extract("""SELECTEDVALUE([X], "a""b") + [Y]""");

        Assert.Equal(["X", "Y"], references.Select(r => r.Object));
    }

    [Fact]
    public void MultipleReferences_SpansAreExact()
    {
        const string expression = "DIVIDE([Net], 'Sales'[Total]) -- [Ignored]";
        var references = DaxReferenceExtractor.Extract(expression);

        Assert.Equal(2, references.Count);
        AssertSpan("[Net]", expression, references[0]);
        AssertSpan("'Sales'[Total]", expression, references[1]);
    }

    [Fact]
    public void NumberLiterals_DoNotShedIdentifiers()
    {
        Assert.Empty(DaxReferenceExtractor.Extract("1e5 + 2.5 + 100"));
    }

    [Fact]
    public void EmptyAndNull_YieldNothing()
    {
        Assert.Empty(DaxReferenceExtractor.Extract(null));
        Assert.Empty(DaxReferenceExtractor.Extract("  "));
    }

    [Fact]
    public void UnterminatedBracket_ConsumesToEndWithoutThrowing()
    {
        var reference = Assert.Single(DaxReferenceExtractor.Extract("[Broken"));

        Assert.Equal("Broken", reference.Object);
    }

    private static void AssertSpan(
        string expected, string expression, DaxReferenceExtractor.DaxReference reference)
        => Assert.Equal(expected, expression[reference.Start..(reference.End + 1)]);
}
