using System.Globalization;

namespace Tomix.App.Test;

/// <summary>
/// The single authority for converting query cell values (the primitive set guaranteed by
/// <see cref="Core.Models.ModelQueryResult"/>: string, long, double, decimal, bool, DateTime,
/// or null) to the canonical invariant strings stored in <c>.expected.json</c> snapshots and
/// compared by <see cref="TestResultComparer"/>. Unlike the display formatting in the CLI
/// renderer, this round-trips without loss (double uses "R"; DateTime keeps fractional seconds).
/// </summary>
public static class TestValueFormatter
{
    /// <summary>Formats a cell to its canonical snapshot string; null (DAX BLANK) stays null.</summary>
    public static string? Format(object? value) => value switch
    {
        null => null,
        string s => s,
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        // ISO-8601; the '.' and fraction digits are omitted entirely when the fraction is zero.
        DateTime dt => dt.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };
}
