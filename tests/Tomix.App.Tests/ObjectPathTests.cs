using Tomix.Core.Paths;

namespace Tomix.App.Tests;

/// <summary>
/// Quoting rules for the path-filter language: a quote only opens a group at segment start,
/// a doubled quote inside a group is a literal apostrophe, and apostrophes anywhere else are
/// ordinary characters (so names like "KPI'er" need no quoting).
/// </summary>
public sealed class ObjectPathTests
{
    private static string[] Texts(string path)
        => ObjectPath.Parse(path).Select(s => s.Text).ToArray();

    [Fact]
    public void BareApostropheName_IsOneLiteralSegment()
    {
        var segments = ObjectPath.Parse("Høreprøver KPI'er");

        var segment = Assert.Single(segments);
        Assert.Equal("Høreprøver KPI'er", segment.Text);
        Assert.False(segment.IsQuoted);
        Assert.True(segment.IsExactLiteral);
    }

    [Fact]
    public void QuotedName_DoubledQuoteIsLiteralApostrophe()
    {
        var segments = ObjectPath.Parse("'Høreprøver KPI''er'");

        var segment = Assert.Single(segments);
        Assert.Equal("Høreprøver KPI'er", segment.Text);
        Assert.True(segment.IsQuoted);
    }

    [Fact]
    public void ApostropheNames_WorkInMultiSegmentPaths()
        => Assert.Equal(["Sales", "O'Brien"], Texts("Sales/O'Brien"));

    [Fact]
    public void QuotedSegment_MayContainSlash()
        => Assert.Equal(["A/B"], Texts("'A/B'"));

    [Fact]
    public void QuotedKeyword_StaysQuoted()
    {
        var segment = Assert.Single(ObjectPath.Parse("'Measures'"));
        Assert.True(segment.IsQuoted);
        Assert.False(segment.IsKeyword);
    }

    [Fact]
    public void QuoteMidSegment_DoesNotOpenAGroup()
        => Assert.Equal(["a'b", "c'd"], Texts("a'b/c'd"));

    [Fact]
    public void EmptyQuotedSegment_IsPreserved()
    {
        var segment = Assert.Single(ObjectPath.Parse("''"));
        Assert.Equal("", segment.Text);
        Assert.True(segment.IsQuoted);
    }
}
