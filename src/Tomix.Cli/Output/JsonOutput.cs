using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tomix.Cli.Output;

/// <summary>
/// The single place that serialises command results to JSON, so the <c>--format json</c>
/// contract (indentation, enum-as-string) is identical for every command.
/// </summary>
internal static class JsonOutput
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static void Write<T>(T value)
        => Console.WriteLine(JsonSerializer.Serialize(value, Options));

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
