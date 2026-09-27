using Tomix.Core.Diagnostics;

namespace Tomix.Core.Tests;

public sealed class SourcePositionTests
{
    [Theory]
    [InlineData("abc", 0, 1, 1)]
    [InlineData("abc", 2, 1, 3)]
    [InlineData("abc", 3, 1, 4)]           // one past the end: where a missing token would go
    [InlineData("ab\ncd", 3, 2, 1)]
    [InlineData("ab\r\ncd", 5, 2, 2)]      // CRLF counts as one line break
    [InlineData("a\n\n\tb", 4, 3, 2)]      // a tab is one column
    [InlineData("", 0, 1, 1)]
    public void Of_ReturnsOneBasedLineAndColumn(string text, int offset, int line, int column)
        => Assert.Equal((line, column), SourcePosition.Of(text, offset));
}
