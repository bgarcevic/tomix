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

    [Theory]
    [InlineData("// tx: rows=3", 3, null, null)]
    [InlineData("// tx: rows>=1", null, 1, null)]
    [InlineData("// tx: rows<=10", null, null, 10)]
    [InlineData("// tx: rows>=1 rows<=10", null, 1, 10)]
    [InlineData("// tx: rows=0, rows>=0", 0, 0, null)]
    [InlineData("-- TX: Rows>=2", null, 2, null)]
    [InlineData("// tx: rows=1\n// tx: rows=4", 4, null, null)]  // the last one wins
    public void WithDirectives_ReadsRowAssertions(string header, int? rows, int? minRows, int? maxRows)
    {
        TestCompareOptions.Exact.WithDirectives(header + "\nEVALUATE T", out var assertions, out var error);

        Assert.Null(error);
        Assert.Equal((rows, minRows, maxRows), (assertions.Rows, assertions.MinRows, assertions.MaxRows));
        Assert.True(assertions.ChecksResult);
    }

    [Fact]
    public void WithDirectives_ColumnsTakeTheRestOfTheLine()
    {
        var options = TestCompareOptions.Exact.WithDirectives(
            "// tx: tolerance=0.01 columns='Sales Data'[Region],  [Total Sales] ,[Margin %]\nEVALUATE T",
            out var assertions, out var error);

        Assert.Null(error);
        Assert.Equal(0.01, options!.Tolerance?.Value);
        Assert.Equal(["'Sales Data'[Region]", "[Total Sales]", "[Margin %]"], assertions.Columns);
    }

    [Theory]
    [InlineData("// tx: expect-error", null)]
    [InlineData("// tx: expect-error ignore-row-order", null)]
    [InlineData("// tx: expect-error=Column 'Foo', in table .*", "Column 'Foo', in table .*")]
    public void WithDirectives_ReadsExpectError(string header, string? pattern)
    {
        TestCompareOptions.Exact.WithDirectives(header + "\nEVALUATE T", out var assertions, out var error);

        Assert.Null(error);
        Assert.True(assertions.ExpectError);
        Assert.Equal(pattern, assertions.ErrorPattern?.ToString());
        Assert.False(assertions.ChecksResult);
    }

    [Theory]
    [InlineData("// tx: rows>=1", false, false)]
    [InlineData("// tx: rows>=1 snapshot", true, true)]
    [InlineData("// tx: SNAPSHOT\n// tx: columns=[A]", true, true)]
    [InlineData("// tx: snapshot", true, true)]
    [InlineData("// tx: tolerance=0.01", false, true)]
    public void WithDirectives_ReadsSnapshot(string header, bool snapshot, bool usesSnapshot)
    {
        TestCompareOptions.Exact.WithDirectives(header + "\nEVALUATE T", out var assertions, out var error);

        Assert.Null(error);
        Assert.Equal((snapshot, usesSnapshot), (assertions.Snapshot, assertions.UsesSnapshot));
    }

    [Fact]
    public void WithDirectives_HasNoAssertions_WhenTheTestSetsNone()
    {
        TestCompareOptions.Exact.WithDirectives("// tx: tolerance=0.01\nEVALUATE T", out var assertions, out _);

        Assert.Same(TestAssertions.None, assertions);
    }

    [Theory]
    [InlineData("// tx: rows", "takes a whole number")]
    [InlineData("// tx: rows=-1", "takes a whole number")]
    [InlineData("// tx: rows>=many", "rows>= takes a whole number")]
    [InlineData("// tx: rows<=", "rows<= takes a whole number")]
    [InlineData("// tx: rows<3", "Unknown directive 'rows<3'")]
    [InlineData("// tx: columns=", "takes the column names")]
    [InlineData("// tx: columns= , ", "takes the column names")]
    [InlineData("// tx: expect-error=([unclosed", "not a valid regular expression")]
    [InlineData("// tx: expect-error\n// tx: rows=0", "cannot be combined")]
    [InlineData("// tx: columns=[A]\n// tx: expect-error", "cannot be combined")]
    [InlineData("// tx: snapshot expect-error", "cannot be combined")]
    [InlineData("// tx: snapshot=true", "snapshot takes no value")]
    public void WithDirectives_RejectsBadAssertions(string header, string message)
    {
        var options = TestCompareOptions.Exact.WithDirectives(header + "\nEVALUATE T", out _, out var error);

        Assert.Null(options);
        Assert.Contains(message, error);
    }
}
