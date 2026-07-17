using System.CommandLine;
using Tomix.App.Query;
using Tomix.Cli.Output;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

/// <summary>
/// Executes a DAX (<c>EVALUATE</c>) or DMV (<c>SELECT ... FROM $SYSTEM....</c>) query against
/// a live model and renders the rowset. Thin CLI shell over <see cref="QueryModelHandler"/>;
/// query text comes from <c>-q</c>, <c>--file</c>, or stdin (<c>-</c> sentinel or implicit pipe).
/// </summary>
internal sealed class QueryCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public QueryCommand(IReadOnlyList<IModelProvider> providers) => _providers = providers;

    public Command Build()
    {
        var queryOption = new Option<string?>("--query")
        {
            Description = "Inline query text ('-' = read from stdin)."
        };
        queryOption.Aliases.Add("-q");

        var fileOption = new Option<string?>("--file")
        {
            Description = "Read the query from a file ('-' = read from stdin)."
        };

        var paramOption = new Option<string[]>("--param")
        {
            Description = "Query parameter as name=value, referenced as @name in DAX. Repeatable.",
            Arity = ArgumentArity.ZeroOrMore
        };

        var limitOption = new Option<int?>("--limit")
        {
            Description = "Maximum number of rows to return."
        };
        limitOption.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int?>() is < 1)
                result.AddError("--limit must be at least 1.");
        });

        var outputFileOption = new Option<string?>("--output-file")
        {
            Description = "Write results to a file as json or csv (from --output-format, else the file extension)."
        };
        outputFileOption.Aliases.Add("-o");

        var noValidateOption = new Option<bool>("--no-validate")
        {
            Description = "Skip the EVALUATE/DEFINE/SELECT keyword pre-check and send the text as-is."
        };

        var command = new Command("query", "Execute a DAX or DMV query against a live model (-q inline, --file, or stdin)")
        {
            queryOption,
            fileOption,
            paramOption,
            limitOption,
            outputFileOption,
            noValidateOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            var errorFormat = parseResult.GetValue(GlobalOptions.ErrorFormat);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            if (!CommandOutput.TryValidateFormat(format, "query", OutputFormats.Text, OutputFormats.Json, OutputFormats.Csv))
                return 2;

            var (query, inputError) = ResolveQueryInput(
                parseResult.GetValue(queryOption),
                parseResult.GetValue(fileOption));
            if (inputError is not null)
            {
                ErrorOutput.Write(new[] { inputError }, errorFormat);
                return 2;
            }

            var parameters = ParseParams(parseResult.GetValue(paramOption), out var badParam);
            if (parameters is null)
            {
                ErrorOutput.Write(
                    new[]
                    {
                        new TomixDiagnostic(
                            "TOMIX_QUERY_BAD_PARAM",
                            DiagnosticSeverity.Error,
                            $"Invalid --param value '{badParam}'. Expected name=value.",
                            Hint: "Example: --param color=Red (referenced as @color in the query)")
                    },
                    errorFormat);
                return 2;
            }

            // Resolve the -o file format up front so a bad combination fails before the query runs.
            var outputFile = parseResult.GetValue(outputFileOption);
            string? fileFormat = null;
            if (!string.IsNullOrWhiteSpace(outputFile))
            {
                // --output-format has a "text" DefaultValueFactory, so only a non-implicit
                // option result counts as user intent; an unspecified format is inferred
                // from the extension while an explicit text (no file representation) is rejected.
                var explicitFormat = parseResult.GetResult(GlobalOptions.OutputFormat) is { Implicit: false }
                    ? format
                    : null;
                fileFormat = ResolveOutputFileFormat(outputFile, explicitFormat);
                if (fileFormat is null)
                {
                    ErrorOutput.Write(
                        new[]
                        {
                            new TomixDiagnostic(
                                "TOMIX_QUERY_OUTPUT_FORMAT",
                                DiagnosticSeverity.Error,
                                "'tx query --output-file' writes json or csv.",
                                Hint: "Use --output-format json|csv or a .json/.csv extension.")
                        },
                        errorFormat);
                    return 2;
                }
            }

            var request = new QueryModelRequest(
                Model: GlobalOptions.ModelValue(parseResult),
                Server: parseResult.GetValue(GlobalOptions.Server),
                Database: parseResult.GetValue(GlobalOptions.Database),
                Auth: GlobalOptions.AuthValue(parseResult),
                Query: query,
                Parameters: parameters.Count > 0 ? parameters : null,
                Limit: parseResult.GetValue(limitOption),
                NoValidate: parseResult.GetValue(noValidateOption));

            var result = await CliSpinner.RunAsync(
                "Running query...",
                () => new QueryModelHandler(_providers).HandleAsync(request, cancellationToken),
                suppress: quiet || OutputFormats.IsJson(format) || OutputFormats.IsCsv(format));

            if (fileFormat is not null)
            {
                if (result.Data is null)
                {
                    ErrorOutput.Write(result.Diagnostics, errorFormat);
                    return result.ExitCode == 0 ? 1 : result.ExitCode;
                }

                QueryResultRenderer.WriteFile(result.Data, outputFile!, fileFormat);
                if (!quiet)
                    Console.Error.WriteLine($"Wrote {result.Data.RowCount} row(s) to {outputFile}");
                return result.ExitCode;
            }

            return CommandOutput.Render(
                result,
                format,
                data => QueryResultRenderer.Render(data, quiet),
                data => data,
                renderCsv: QueryResultRenderer.RenderCsv,
                errorFormat: errorFormat);
        });

        return command;
    }

    /// <summary>
    /// Resolves the query text from <c>-q</c>, <c>--file</c>, or stdin (explicit <c>-</c> sentinel
    /// on either option, or an implicit pipe when both are absent). Returns a diagnostic instead
    /// of text when the flags conflict or the file is missing; a missing query is not an error
    /// here — the handler reports TOMIX_QUERY_REQUIRED.
    /// </summary>
    internal static (string? Query, TomixDiagnostic? Error) ResolveQueryInput(string? query, string? file)
    {
        if (!string.IsNullOrWhiteSpace(query) && !string.IsNullOrWhiteSpace(file))
            return (null, new TomixDiagnostic(
                "TOMIX_QUERY_INPUT_CONFLICT",
                DiagnosticSeverity.Error,
                "Pass either -q or --file, not both.",
                Hint: "Use -q \"EVALUATE ...\" for inline text or --file query.dax for a file."));

        if (!string.IsNullOrWhiteSpace(file) && file != "-" && !File.Exists(file))
            return (null, new TomixDiagnostic(
                "TOMIX_QUERY_FILE_NOT_FOUND",
                DiagnosticSeverity.Error,
                $"Query file not found: {file}"));

        return (file == "-" ? InputValueResolver.Resolve("-") : InputValueResolver.Resolve(query, file), null);
    }

    /// <summary>
    /// Parses repeatable <c>--param name=value</c> tokens (a leading <c>@</c> on the name is
    /// tolerated). Empty input yields an empty dictionary; a malformed token yields null with
    /// the offending value in <paramref name="badValue"/>. Mirrors <see cref="RefreshCommand.ParsePartitions"/>.
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? ParseParams(string[]? raw, out string? badValue)
    {
        badValue = null;
        if (raw is null || raw.Length == 0)
            return new Dictionary<string, string>();

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in raw)
        {
            var eq = value.IndexOf('=');
            var name = eq > 0 ? value[..eq].TrimStart('@').Trim() : "";
            if (name.Length == 0)
            {
                badValue = value;
                return null;
            }
            parameters[name] = value[(eq + 1)..];
        }
        return parameters;
    }

    /// <summary>
    /// Picks the <c>-o</c> file format: an explicit <c>--output-format json|csv</c> wins;
    /// when unspecified or <c>auto</c>, a <c>.json</c> extension means json, anything else csv.
    /// Returns null for formats with no file representation (explicit <c>text</c>).
    /// </summary>
    internal static string? ResolveOutputFileFormat(string outputFile, string? rawFormat)
    {
        if (OutputFormats.IsJson(rawFormat ?? ""))
            return OutputFormats.Json;
        if (OutputFormats.IsCsv(rawFormat ?? ""))
            return OutputFormats.Csv;
        if (rawFormat is null or OutputFormats.Auto)
            return Path.GetExtension(outputFile).Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? OutputFormats.Json
                : OutputFormats.Csv;
        return null;
    }
}
