using Tomix.App.Format;

namespace Tomix.App.Tests;

public sealed class OfflineMFormatterClientTests
{
    private const string LongStep = """
        let
            Source = Table.TransformColumnTypes(Source, {{"Category", type text}, {"Sorting", Int64.Type}})
        in
            Source
        """;

    private readonly OfflineMFormatterClient _client = new();

    [Theory]
    [InlineData("m", true)]
    [InlineData("powerquery", true)]
    [InlineData("dax", false)]
    public void CanFormat_OnlyPowerQuery(string language, bool expected)
    {
        FormatterLanguages.TryNormalize(language, out var normalized);

        Assert.Equal(expected, _client.CanFormat(normalized));
    }

    [Fact]
    public async Task FormatAsync_TrimsTheTrailingNewline()
    {
        var response = await FormatAsync("let x = 1 in x");

        Assert.True(response.Success, string.Join("; ", response.Errors));
        Assert.Equal("let\n    x = 1\nin\n    x", response.Formatted);
        Assert.Empty(response.Errors);
    }

    [Fact]
    public async Task FormatAsync_Long_WrapsAtAWiderWidth()
    {
        var narrow = await FormatAsync(LongStep, isLong: false);
        var wide = await FormatAsync(LongStep, isLong: true);

        Assert.True(narrow.Success && wide.Success);
        Assert.True(narrow.Formatted.Split('\n').Length > wide.Formatted.Split('\n').Length);
    }

    [Fact]
    public async Task FormatAsync_UsesLfAndFourSpaceIndentation()
    {
        var response = await FormatAsync("let\r\n\tx = 1\r\nin\r\n\tx");

        Assert.Equal("let\n    x = 1\nin\n    x", response.Formatted);
    }

    [Fact]
    public async Task FormatAsync_ParseError_ReportsLineAndColumnAndKeepsTheOriginal()
    {
        const string broken = "let\n    x = 1,\nin\n    x";

        var response = await FormatAsync(broken);

        Assert.False(response.Success);
        Assert.Equal(broken, response.Formatted);
        Assert.StartsWith("M syntax error on line 3, column 1: ", Assert.Single(response.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FormatAsync_LexError_ReportsLineAndColumn()
    {
        var response = await FormatAsync("let x = \"abc in x");

        Assert.False(response.Success);
        Assert.Equal("M syntax error on line 1, column 9: Unterminated string", Assert.Single(response.Errors));
    }

    [Fact]
    public async Task FormatAsync_Cancelled_Throws()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _client.FormatAsync(
            new ExpressionFormatRequest("let x = 1 in x", FormatterLanguages.PowerQuery, Long: false),
            new CancellationToken(canceled: true)));
    }

    private Task<ExpressionFormatResponse> FormatAsync(string expression, bool isLong = false)
        => _client.FormatAsync(
            new ExpressionFormatRequest(expression, FormatterLanguages.PowerQuery, isLong),
            CancellationToken.None);
}
