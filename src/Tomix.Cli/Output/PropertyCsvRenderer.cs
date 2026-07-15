using Tomix.Core.Properties;

namespace Tomix.Cli.Output;

/// <summary>
/// Renders catalog-projected property dictionaries as CSV: one column per descriptor
/// (<see cref="PropertyDescriptor.Header"/> as header, values looked up by JSON key), with
/// optional extra leading columns for envelope data such as an object's path.
/// </summary>
internal static class PropertyCsvRenderer
{
    public static void Write(
        IReadOnlyList<PropertyDescriptor> descriptors,
        IEnumerable<(IReadOnlyList<object?> Leading, IReadOnlyDictionary<string, object?> Projected)> rows,
        params string[] leadingHeaders)
    {
        CsvOutput.Write(
            [.. leadingHeaders, .. descriptors.Select(d => d.Header)],
            rows.Select(row => (IReadOnlyList<object?>)
                [.. row.Leading, .. descriptors.Select(d => row.Projected.GetValueOrDefault(d.JsonKey))]));
    }

    public static void Write(
        IReadOnlyList<PropertyDescriptor> descriptors,
        IReadOnlyDictionary<string, object?> projected)
        => Write(descriptors, [(Array.Empty<object?>(), projected)]);
}
