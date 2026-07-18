using System.CommandLine;
using Tomix.Cli.Commands;
using Tomix.Core.Diagnostics;
using Tomix.Core.Results;

namespace Tomix.Cli.Output;

/// <summary>
/// Shared bridge between handler results and the console: validates the format option,
/// renders either JSON or a command-specific human view, prints diagnostics to stderr,
/// and maps the result to an exit code.
/// </summary>
internal static class CommandOutput
{
    /// <summary>
    /// Validates <paramref name="format"/>, writing an error to stderr if it is unrecognised.
    /// Returns <c>false</c> so the command can exit with code 2 (invalid arguments).
    /// </summary>
    public static bool TryValidateFormat(string format)
        => ValidateFormatValue(format, errorFormat: null);

    /// <summary>
    /// Like <see cref="TryValidateFormat(string)"/>, but additionally rejects formats the command
    /// cannot render (instead of silently falling back to text). <c>auto</c> is always accepted.
    /// </summary>
    public static bool TryValidateFormat(string format, string commandName, params string[] supported)
        => ValidateFormat(format, errorFormat: null, commandName, supported);

    /// <summary>Overload that honors the command's <c>--error-format</c> value.</summary>
    public static bool TryValidateFormat(ParseResult parseResult, string format, string commandName, params string[] supported)
        => ValidateFormat(format, parseResult.GetValue(GlobalOptions.ErrorFormat), commandName, supported);

    private static bool ValidateFormatValue(string format, string? errorFormat)
    {
        if (OutputFormats.IsValid(format))
            return true;

        ErrorOutput.Write(
            [new TomixDiagnostic(
                "TOMIX_INVALID_OUTPUT_FORMAT",
                DiagnosticSeverity.Error,
                "Invalid --output-format value. Expected: auto, text, json, csv, tmsl, bim, or tmdl.")],
            errorFormat);
        return false;
    }

    private static bool ValidateFormat(string format, string? errorFormat, string commandName, string[] supported)
    {
        if (!ValidateFormatValue(format, errorFormat))
            return false;

        if (format is OutputFormats.Auto || supported.Contains(format, StringComparer.OrdinalIgnoreCase))
            return true;

        ErrorOutput.Write(
            [new TomixDiagnostic(
                "TOMIX_OUTPUT_FORMAT_UNSUPPORTED",
                DiagnosticSeverity.Error,
                $"'tx {commandName}' does not support --output-format {format}. Supported: {string.Join(", ", supported)}.")],
            errorFormat);
        return false;
    }

    /// <summary>
    /// Renders a successful result (JSON or human) or prints its diagnostics, returning the exit code.
    /// Branches on <c>Data</c> rather than <c>Success</c> so commands like <c>doctor</c> can still
    /// render their report while signalling a non-zero exit code.
    /// </summary>
    public static int Render<T>(TomixResult<T> result, string format, Action<T> renderHuman)
        => Render(result, format, renderHuman, data => data);

    public static int Render<T>(
        TomixResult<T> result,
        string format,
        string? errorFormat,
        Action<T> renderHuman)
        => Render(result, format, renderHuman, data => data, renderCsv: null, errorFormat);

    public static int Render<T>(
        TomixResult<T> result,
        string format,
        Action<T> renderHuman,
        Action<T> renderCsv)
        => Render(result, format, renderHuman, data => data, renderCsv);

    public static int Render<T>(
        TomixResult<T> result,
        string format,
        string? errorFormat,
        Action<T> renderHuman,
        Action<T> renderCsv)
        => Render(result, format, renderHuman, data => data, renderCsv, errorFormat);

    public static int Render<T, TJson>(
        TomixResult<T> result,
        string format,
        Action<T> renderHuman,
        Func<T, TJson> projectJson,
        Action<T>? renderCsv = null,
        string? errorFormat = null)
    {
        if (result.Data is null)
        {
            ErrorOutput.Write(result.Diagnostics, errorFormat);
            return result.ExitCode == 0 ? 1 : result.ExitCode;
        }

        if (OutputFormats.IsJson(format))
            JsonOutput.Write(projectJson(result.Data));
        else if (OutputFormats.IsCsv(format) && renderCsv is not null)
            renderCsv(result.Data);
        else
            renderHuman(result.Data);

        return result.ExitCode;
    }
}
