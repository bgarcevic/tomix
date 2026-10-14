using Tomix.App.Test;

namespace Tomix.App.Tests;

public sealed class TestCompareOptionsTests
{
    [Theory]
    [InlineData("0.01", 0.01, false)]
    [InlineData("0", 0.0, false)]
    [InlineData("1e-6", 1e-6, false)]
    [InlineData("0.1%", 0.001, true)]
    [InlineData(" 5 % ", 0.05, true)]
    public void TryParse_AcceptsAbsoluteAndPercentage(string text, double value, bool relative)
    {
        Assert.True(TestTolerance.TryParse(text, out var tolerance, out var error));
        Assert.Null(error);
        Assert.Equal(value, tolerance!.Value, precision: 12);
        Assert.Equal(relative, tolerance.Relative);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-0.1")]
    [InlineData("%")]
    [InlineData("NaN")]
    [InlineData("1,5")]
    public void TryParse_RejectsInvalidValues(string text)
    {
        Assert.False(TestTolerance.TryParse(text, out var tolerance, out var error));
        Assert.Null(tolerance);
        Assert.Contains($"'{text}'", error);
    }

    [Theory]
    [InlineData("// tx: tolerance=0.01\nEVALUATE T", 0.01, false)]
    [InlineData("-- tx: ignore-row-order\nEVALUATE T", null, true)]
    [InlineData("// tx: tolerance=0.5% ignore-row-order\nEVALUATE T", 0.005, true)]
    [InlineData("// tx: tolerance=0.01, ignore-row-order=true\nEVALUATE T", 0.01, true)]
    [InlineData("// Sales by region\n\n//TX:  Tolerance=0.01\r\nEVALUATE T", 0.01, false)]
    [InlineData("EVALUATE T\n// tx: tolerance=0.01", null, false)]       // after the statement: not a directive
    [InlineData("/* tx: tolerance=0.01 */\nEVALUATE T", null, false)]    // block comments end the header
    [InlineData("// a comment mentioning tx: in passing\nEVALUATE T", null, false)]
    public void WithDirectives_ReadsTheLeadingCommentLines(string query, double? tolerance, bool ignoreRowOrder)
    {
        var options = TestCompareOptions.Exact.WithDirectives(query, out var error);

        Assert.Null(error);
        Assert.Equal(tolerance, options!.Tolerance?.Value);
        Assert.Equal(ignoreRowOrder, options.IgnoreRowOrder);
    }

    [Fact]
    public void WithDirectives_OverridesRunDefaults()
    {
        var defaults = new TestCompareOptions(new TestTolerance(0.5, Relative: false), IgnoreRowOrder: true);

        var options = defaults.WithDirectives("// tx: tolerance=0 ignore-row-order=false\nEVALUATE T", out _);

        Assert.Null(options!.Tolerance);
        Assert.False(options.IgnoreRowOrder);
    }

    [Fact]
    public void WithDirectives_KeepsRunDefaults_WhenTheTestHasNone()
    {
        var defaults = new TestCompareOptions(new TestTolerance(0.5, Relative: false), IgnoreRowOrder: true);

        Assert.Equal(defaults, defaults.WithDirectives("EVALUATE T", out _));
    }

    [Theory]
    [InlineData("// tx: tolerence=0.01", "Unknown directive 'tolerence=0.01'")]
    [InlineData("// tx: tolerance=abc", "Invalid tolerance 'abc'")]
    [InlineData("// tx: tolerance", "takes a value")]
    [InlineData("// tx: ignore-row-order=maybe", "true or false")]
    public void WithDirectives_RejectsUnknownKeysAndBadValues(string header, string message)
    {
        var options = TestCompareOptions.Exact.WithDirectives(header + "\nEVALUATE T", out var error);

        Assert.Null(options);
        Assert.Contains(message, error);
    }
}
