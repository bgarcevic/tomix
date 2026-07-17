using System.Globalization;

namespace Tomix.Cli.Output;

internal static class CsvOutput
{
    public static void Write(
        IReadOnlyList<string> headers,
        IEnumerable<IReadOnlyList<object?>> rows)
        => Write(Console.Out, headers, rows);

    public static void Write(
        TextWriter writer,
        IReadOnlyList<string> headers,
        IEnumerable<IReadOnlyList<object?>> rows)
    {
        writer.WriteLine(string.Join(",", headers.Select(Escape)));

        foreach (var row in rows)
            writer.WriteLine(string.Join(",", row.Select(value => Escape(Format(value)))));
    }

    public static void WriteValue(object? value)
        => Console.WriteLine(Format(value));

    private static string Format(object? value)
        => value switch
        {
            null => "",
            bool b => b ? "True" : "False",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
            _ => value.ToString() ?? ""
        };

    private static string Escape(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
            return value;

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
