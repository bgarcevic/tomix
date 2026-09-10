using Tomix.App.Format;

namespace Tomix.App.Tests;

public sealed class OfflineDaxFormatterClientTests
{
    [Theory]
    [InlineData("dax", true)]
    [InlineData("DAX", true)]
    [InlineData("powerquery", false)]
    [InlineData("m", false)]
    public void CanFormat_AcceptsOnlyDax(string language, bool expected)
        => Assert.Equal(expected, new OfflineDaxFormatterClient().CanFormat(language));

    [Fact]
    public async Task FormatAsync_FormatsOffline_ReturnsFormattedText()
    {
        var response = await new OfflineDaxFormatterClient().FormatAsync(
            new ExpressionFormatRequest("CALCULATE(sum(sales[amt]))", FormatterLanguages.Dax, Long: false),
            CancellationToken.None);

        Assert.True(response.Success);
        Assert.Empty(response.Errors);
        Assert.Equal("CALCULATE ( SUM ( sales[amt] ) )", response.Formatted);
    }

    [Fact]
    public async Task FormatAsync_FormatterDeclines_ReturnsFailureWithOriginalText()
    {
        const string expression = "SUM(Sales[Amount]) -- total sales\n/* block\n   comment */\n+ [Cost]";

        var response = await new OfflineDaxFormatterClient().FormatAsync(
            new ExpressionFormatRequest(expression, FormatterLanguages.Dax, Long: false),
            CancellationToken.None);

        Assert.False(response.Success);
        var error = Assert.Single(response.Errors);
        Assert.Contains("without changing its code", error);
        Assert.Contains("line 1", error);
        Assert.Equal(expression, response.Formatted);
    }

    [Fact]
    public async Task FormatAsync_SyntaxErrors_ReportsLineAndMessage()
    {
        var response = await new OfflineDaxFormatterClient().FormatAsync(
            new ExpressionFormatRequest("SUM(Sales[Amount]", FormatterLanguages.Dax, Long: false),
            CancellationToken.None);

        Assert.False(response.Success);
        var error = Assert.Single(response.Errors);
        Assert.Contains("line 1", error);
        Assert.Contains("has no matching", error);
        Assert.Equal("SUM(Sales[Amount]", response.Formatted);
    }
}
