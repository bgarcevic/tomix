using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using Tomix.Core.Diagnostics;

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

            var errorObj = new Dictionary<string, object?>
            {
                ["error"] = error?.Message ?? "",
                ["code"] = error?.Code,
                ["severity"] = error?.Severity.ToString(),
                ["hint"] = error?.Hint
            };
            if (detail is not null)
                errorObj["detail"] = detail;
            if (error?.Blocked is { } blocked)
                errorObj["blocked"] = blocked;
            if (error?.Reason is { } reason)
                errorObj["reason"] = reason;
            if (error?.NewValidationErrorCount is { } count)
                errorObj["newValidationErrorCount"] = count;
            if (error?.NewErrors is { } newErrors)
                errorObj["newErrors"] = newErrors.Select(issue => new
                {
                    code = issue.Code,
                    message = issue.Message,
                    @object = issue.Object
                }).ToList();

            Console.Error.WriteLine(JsonSerializer.Serialize(errorObj, Options));
            return;
        }

        var errConsole = StdErr.Console();

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

            if (diagnostic.NewErrors is { Count: > 0 } errors)
            {
                foreach (var issue in errors.Take(10))
                    errConsole.MarkupLine($"  {Styling.MarkupEscape(issue.Code)} {Styling.MarkupEscape(issue.Message)} (in {Styling.MarkupEscape(issue.Object)})");
                if (errors.Count > 10)
                    errConsole.MarkupLine($"  {Styling.MarkupEscape($"... and {errors.Count - 10} more")}");
            }
        }

        // Plain write: stack traces contain characters Spectre would treat as markup.
        if (detail is not null)
            Console.Error.WriteLine(detail);
    }
}
