using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tomix.Core.Diagnostics;
using Spectre.Console;

namespace Tomix.Cli.Output;

internal static class ErrorOutput
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    /// <summary>
    /// Renders diagnostics on stderr in the requested error format. <paramref name="detail"/>
    /// carries free-form debug text (e.g. a stack trace under <c>--debug</c>); in JSON mode it
    /// is embedded as a <c>detail</c> field so stderr stays one valid JSON document, never
    /// appended as raw text after the envelope.
    /// </summary>
    public static void Write(IReadOnlyList<TomixDiagnostic> diagnostics, string? format, string? detail = null)
    {
        if (string.Equals(format, OutputFormats.Json, StringComparison.OrdinalIgnoreCase))
        {
            var error = diagnostics.FirstOrDefault(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Fatal)
                ?? diagnostics.FirstOrDefault();

            var errorObj = new Dictionary<string, string?>
            {
                ["error"] = error?.Message ?? "",
                ["code"] = error?.Code,
                ["severity"] = error?.Severity.ToString(),
                ["hint"] = error?.Hint
            };
            if (detail is not null)
                errorObj["detail"] = detail;

            Console.Error.WriteLine(JsonSerializer.Serialize(errorObj, Options));
            return;
        }

        var errConsole = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(Console.Error)
        });

        foreach (var diagnostic in diagnostics)
        {
            var label = diagnostic.Severity switch
            {
                DiagnosticSeverity.Info => Styling.SeverityMarkup("Info"),
                DiagnosticSeverity.Warning => Styling.SeverityMarkup("Warning"),
                _ => Styling.SeverityMarkup("Error")
            };
            var message = Styling.MarkupEscape(diagnostic.Message);
            errConsole.MarkupLine($"{label}: {message}");

            if (!string.IsNullOrEmpty(diagnostic.Hint))
                errConsole.MarkupLine($"  {Styling.Guidance($"→ {diagnostic.Hint}")}");
        }

        // Plain write: stack traces contain characters Spectre would treat as markup.
        if (detail is not null)
            Console.Error.WriteLine(detail);
    }
}
