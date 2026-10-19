using System.Globalization;

namespace Tomix.App.Test;

/// <summary>
/// Numeric tolerance for snapshot comparison: an absolute difference (<c>0.01</c>) or, with a
/// <c>%</c> suffix, a difference relative to the larger of the two magnitudes (<c>0.1%</c>).
/// A tolerance of zero means exact comparison.
/// </summary>
public sealed record TestTolerance(double Value, bool Relative)
{
    /// <summary>Parses <c>0.01</c> or <c>0.1%</c>; rejects negative, non-finite, or malformed values.</summary>
    public static bool TryParse(string? text, out TestTolerance? tolerance, out string? error)
    {
        tolerance = null;
        var trimmed = text?.Trim() ?? "";
        var relative = trimmed.EndsWith('%');
        var number = relative ? trimmed[..^1].TrimEnd() : trimmed;

        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value) || value < 0)
        {
            error = $"Invalid tolerance '{text}'. Expected a non-negative number such as 0.01, or a percentage such as 0.1%.";
            return false;
        }

        error = null;
        tolerance = new TestTolerance(relative ? value / 100 : value, relative);
        return true;
    }

    /// <summary>True when two numbers differ by no more than the tolerance.</summary>
    public bool Accepts(double expected, double actual)
    {
        var difference = Math.Abs(expected - actual);
        var limit = Relative ? Value * Math.Max(Math.Abs(expected), Math.Abs(actual)) : Value;
        return difference <= limit;
    }
}

/// <summary>
/// How <see cref="TestResultComparer"/> compares a snapshot to an actual result. Run-wide
/// defaults come from <c>tx test</c> options; a test overrides them with <c>tx:</c> directives
/// in the comment lines that open its <c>.dax</c> file (see <see cref="WithDirectives"/>).
/// </summary>
/// <param name="Tolerance">Numeric tolerance for int64/double/decimal columns, and for numeric cells of object (Variant) columns; null compares exactly.</param>
/// <param name="IgnoreRowOrder">Sort both rowsets by their values before comparing.</param>
public sealed record TestCompareOptions(TestTolerance? Tolerance = null, bool IgnoreRowOrder = false)
{
    public static readonly TestCompareOptions Exact = new();

    private const string DirectivePrefix = "tx:";

    /// <summary>
    /// Applies the test's own directives on top of these options. Directives sit in the
    /// <c>//</c> or <c>--</c> comment lines before the first statement, for example
    /// <c>// tx: tolerance=0.01 ignore-row-order</c>. Returns null with an
    /// <paramref name="error"/> on an unknown key or bad value, so a typo fails the test
    /// instead of silently comparing exactly.
    /// </summary>
    public TestCompareOptions? WithDirectives(string query, out string? error)
    {
        var options = this;
        error = null;

        var lines = query.ReplaceLineEndings("\n").Split('\n');
        foreach (var body in lines.Take(HeaderLength(lines)).Select(DirectiveBody).OfType<string>())
        {
            foreach (var directive in body.Split(' ', '\t', ','))
            {
                if (directive.Length == 0)
                    continue;

                options = options.Apply(directive, out error);
                if (options is null)
                    return null;
            }
        }

        return options;
    }

    /// <summary>
    /// The query without its <c>tx:</c> directive lines, so adding or changing a test's settings
    /// is not reported as a query change (see <see cref="TestSnapshotFile.ComputeQueryHash"/>).
    /// </summary>
    public static string WithoutDirectives(string query)
    {
        var lines = query.ReplaceLineEndings("\n").Split('\n');
        var header = HeaderLength(lines);
        return string.Join('\n', lines.Where((line, i) => i >= header || DirectiveBody(line) is null));
    }

    /// <summary>Number of leading blank and <c>//</c> or <c>--</c> comment lines.</summary>
    private static int HeaderLength(string[] lines)
    {
        var count = 0;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length > 0 && !IsLineComment(line))
                break;
            count++;
        }

        return count;
    }

    /// <summary>The text after <c>tx:</c> when the line is a directive comment; otherwise null.</summary>
    private static string? DirectiveBody(string rawLine)
    {
        var line = rawLine.Trim();
        if (!IsLineComment(line))
            return null;

        var comment = line[2..].TrimStart();
        return comment.StartsWith(DirectivePrefix, StringComparison.OrdinalIgnoreCase)
            ? comment[DirectivePrefix.Length..]
            : null;
    }

    private static bool IsLineComment(string line)
        => line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("--", StringComparison.Ordinal);

    private TestCompareOptions? Apply(string directive, out string? error)
    {
        error = null;
        var separator = directive.IndexOf('=');
        var key = (separator < 0 ? directive : directive[..separator]).ToLowerInvariant();
        var value = separator < 0 ? null : directive[(separator + 1)..];

        switch (key)
        {
            case "tolerance" when value is not null:
                if (!TestTolerance.TryParse(value, out var tolerance, out error))
                    return null;
                return this with { Tolerance = tolerance!.Value == 0 ? null : tolerance };

            case "tolerance":
                error = "Invalid directive 'tolerance': it takes a value, for example tolerance=0.01 or tolerance=0.1%.";
                return null;

            case "ignore-row-order":
                if (value is null)
                    return this with { IgnoreRowOrder = true };
                if (bool.TryParse(value, out var ignore))
                    return this with { IgnoreRowOrder = ignore };
                error = $"Invalid directive '{directive}': ignore-row-order takes true or false.";
                return null;

            default:
                error = $"Unknown directive '{directive}'. Supported: tolerance=<value>, ignore-row-order[=true|false].";
                return null;
        }
    }
}
