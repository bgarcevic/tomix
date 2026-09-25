using Tomix.Core.M;

namespace Tomix.Core.Tests;

public sealed class MLanguageTests
{
    // Normalized: a CRLF checkout (Windows CI) must not change the anchors below.
    private static readonly string Partition = """
        let
            Source = Sql.Database("srv", "db"),
            #"Changed Type" = Table.TransformColumnTypes(Source, {{"Sorting", Int64.Type}}),
            Filtered = Table.SelectRows(#"Changed Type", each [Sales Amount] > 0.5 and [#"Is Open"] <> null)
        in
            Filtered
        """.ReplaceLineEndings("\n");

    [Theory]
    [InlineData("let", MTextClassification.Keyword)]
    [InlineData("Source =", MTextClassification.DefinitionName)]
    [InlineData("Sql.Database", MTextClassification.Function)]
    [InlineData("\"srv\"", MTextClassification.StringLiteral)]
    [InlineData("#\"Changed Type\" =", MTextClassification.DefinitionName)]
    [InlineData("Table.TransformColumnTypes", MTextClassification.Function)]
    [InlineData("Int64.Type", MTextClassification.Function)]
    [InlineData("Filtered =", MTextClassification.DefinitionName)]
    [InlineData("each", MTextClassification.Keyword)]
    [InlineData("[Sales Amount]", MTextClassification.FieldAccess)]
    [InlineData("0.5", MTextClassification.Number)]
    [InlineData("and", MTextClassification.Keyword)]
    [InlineData("[#\"Is Open\"]", MTextClassification.FieldAccess)]
    [InlineData("<>", MTextClassification.Operator)]
    [InlineData("null)", MTextClassification.Literal)]
    [InlineData("in\n", MTextClassification.Keyword)]
    public void Classify_Partition_ClassifiesEveryRole(string anchor, MTextClassification expected)
        => Assert.Equal(expected, ClassificationOf(Partition, anchor));

    [Fact]
    public void Classify_StepReferences_AreNotDefinitions()
    {
        // `Source` and `#"Changed Type"` as arguments are references, and `Filtered` after `in` is the result.
        Assert.Equal(MTextClassification.Text, ClassificationOf(Partition, "Source, {"));
        Assert.Equal(MTextClassification.Text, ClassificationOf(Partition, "#\"Changed Type\", each"));
        Assert.Equal(MTextClassification.Text, ClassificationOf(Partition, "Filtered", fromEnd: true));
    }

    [Fact]
    public void Classify_Comparisons_AreNotDefinitions()
    {
        const string m = "if a = b then [x] = 1 else f(y)";

        Assert.DoesNotContain(MLanguage.Classify(m), span => span.Classification == MTextClassification.DefinitionName);
        Assert.Equal(MTextClassification.FieldAccess, ClassificationOf(m, "[x]"));
        Assert.Equal(MTextClassification.Function, ClassificationOf(m, "f("));
    }

    [Fact]
    public void Classify_RecordsAndMeta_DefineFieldsInsteadOfAccessingThem()
    {
        const string m = "\"dev\" meta [IsParameterQuery = true, Type = \"Text\"]";

        Assert.Equal(MTextClassification.Keyword, ClassificationOf(m, "meta"));
        Assert.Equal(MTextClassification.DefinitionName, ClassificationOf(m, "IsParameterQuery"));
        Assert.Equal(MTextClassification.Literal, ClassificationOf(m, "true"));
        Assert.Equal(MTextClassification.DefinitionName, ClassificationOf(m, "Type ="));
        Assert.DoesNotContain(MLanguage.Classify(m), span => span.Classification == MTextClassification.FieldAccess);
    }

    [Fact]
    public void Classify_TypeExpressions_TreatPrimitiveTypesAsKeywords()
    {
        const string m = "let _t = ((type nullable text) meta [Serialized.Text = true]) in type table [Category = _t, Sorting = _t]";

        Assert.Equal(MTextClassification.DefinitionName, ClassificationOf(m, "_t ="));
        Assert.Equal(MTextClassification.Keyword, ClassificationOf(m, "nullable"));
        Assert.Equal(MTextClassification.Keyword, ClassificationOf(m, "text)"));
        Assert.Equal(MTextClassification.DefinitionName, ClassificationOf(m, "Serialized.Text"));
        Assert.Equal(MTextClassification.Keyword, ClassificationOf(m, "table ["));
        Assert.Equal(MTextClassification.DefinitionName, ClassificationOf(m, "Category"));
    }

    [Fact]
    public void Classify_PrimitiveTypeNamesOutsideATypePosition_AreText()
    {
        const string m = "let text = 1, date = text in date";

        Assert.Equal(MTextClassification.DefinitionName, ClassificationOf(m, "text ="));
        Assert.Equal(MTextClassification.Text, ClassificationOf(m, "text in"));
    }

    [Theory]
    [InlineData("#table", MTextClassification.Function)]
    [InlineData("#date", MTextClassification.Function)]
    [InlineData("#infinity", MTextClassification.Literal)]
    public void Classify_HashKeywords(string text, MTextClassification expected)
        => Assert.Equal(expected, ClassificationOf($"x = {text}(1)", text));

    [Fact]
    public void Classify_StringsAndComments_ContainMisleadingCharacters()
    {
        const string m = "\"a // \"\"b\"\" /* c\" & x // real comment\n/* block\n let */ 1";

        Assert.Equal(MTextClassification.StringLiteral, ClassificationOf(m, "\"a // \"\"b\"\" /* c\""));
        Assert.Equal(MTextClassification.Comment, ClassificationOf(m, "// real comment"));
        Assert.Equal(MTextClassification.Comment, ClassificationOf(m, "/* block\n let */"));
        Assert.Equal(MTextClassification.Number, ClassificationOf(m, "1"));
    }

    [Theory]
    [InlineData("0xFF", "0xFF")]
    [InlineData("1.5e-3", "1.5e-3")]
    [InlineData(".5", ".5")]
    [InlineData("{1..10}", "1")]
    [InlineData("{1..10}", "10")]
    public void Classify_Numbers(string m, string number)
        => Assert.Equal(MTextClassification.Number, ClassificationOf(m, number));

    [Fact]
    public void Classify_Range_KeepsTheOperatorApartFromTheNumbers()
    {
        var spans = MLanguage.Classify("{1..10}");

        Assert.Equal(
            ["{", "1", "..", "10", "}"],
            spans.Select(span => "{1..10}".Substring(span.Start, span.Length)));
    }

    [Theory]
    [InlineData("let x = \"unterminated")]
    [InlineData("let x = 1 /* unterminated")]
    [InlineData("#\"unterminated")]
    [InlineData("[unterminated")]
    [InlineData("#")]
    [InlineData("")]
    [InlineData("0x")]
    [InlineData("1e")]
    public void Classify_BrokenInput_NeverThrowsAndCoversTheText(string m)
    {
        var spans = MLanguage.Classify(m);

        AssertOrderedAndInBounds(m, spans);
        Assert.Equal(m.Count(c => !char.IsWhiteSpace(c)), spans.Sum(span => m.Substring(span.Start, span.Length).Count(c => !char.IsWhiteSpace(c))));
    }

    [Fact]
    public void Classify_RealPartition_CoversEveryNonWhitespaceCharacterOnce()
    {
        var spans = MLanguage.Classify(Partition);

        AssertOrderedAndInBounds(Partition, spans);
        var covered = new bool[Partition.Length];
        foreach (var span in spans)
        {
            for (var i = span.Start; i < span.Start + span.Length; i++)
                covered[i] = true;
        }

        for (var i = 0; i < Partition.Length; i++)
            Assert.True(covered[i] || char.IsWhiteSpace(Partition[i]), $"Character {i} ('{Partition[i]}') is not covered.");
    }

    private static void AssertOrderedAndInBounds(string m, IReadOnlyList<MClassifiedSpan> spans)
    {
        var position = 0;
        foreach (var span in spans)
        {
            Assert.True(span.Start >= position, "Spans must be ordered and must not overlap.");
            Assert.True(span.Length > 0 && span.Start + span.Length <= m.Length);
            position = span.Start + span.Length;
        }
    }

    /// <summary>The classification of the span that starts where <paramref name="anchor"/> does.</summary>
    private static MTextClassification ClassificationOf(string m, string anchor, bool fromEnd = false)
    {
        var start = fromEnd ? m.LastIndexOf(anchor, StringComparison.Ordinal) : m.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Anchor not found: {anchor}");

        var span = Assert.Single(MLanguage.Classify(m), s => s.Start == start);
        return span.Classification;
    }
}
