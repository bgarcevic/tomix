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

        var traceOption = new Option<string?>("--trace")
        {
            Description = "Show server timings (formula vs storage engine). Add a path to also dump raw trace events. Needs admin rights.",
            Arity = ArgumentArity.ZeroOrOne
        };

        var planOption = new Option<bool>("--plan")
        {
            Description = "Show the logical and physical DAX query plans. Needs admin rights."
        };

        var coldOption = new Option<bool>("--cold")
        {
            Description = "Clear the model cache before each run so timings reflect a cold cache. Needs admin rights."
        };

        var runsOption = new Option<int?>("--runs")
        {
            Description = "Execute the query N times and report per-run timings with Avg/Min/Max/StdDev (default: 1)."
        };
        runsOption.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int?>() is < 1)
                result.AddError("--runs must be at least 1.");
        });

        var command = new Command("query", "Execute a DAX or DMV query against a live model (-q inline, --file, or stdin)")
        {
            queryOption,
            fileOption,
            paramOption,
            limitOption,
            outputFileOption,
            noValidateOption,
            traceOption,
            planOption,
            coldOption,
            runsOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            var errorFormat = parseResult.GetValue(GlobalOptions.ErrorFormat);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            if (!CommandOutput.TryValidateFormat(parseResult, format, "query", OutputFormats.Text, OutputFormats.Json, OutputFormats.Csv))
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

            // --trace present enables server timings; an accompanying value (path or "-") also
            // dumps raw trace events. Bare --trace is summary-only, so it does NOT write a raw dump.
            var traceEnabled = parseResult.GetResult(traceOption) is not null;
            var traceValue = parseResult.GetValue(traceOption);
            var rawTracePath = traceEnabled && !string.IsNullOrEmpty(traceValue) ? traceValue : null;

            var request = new QueryModelRequest(
                Model: GlobalOptions.ModelValue(parseResult),
                Server: parseResult.GetValue(GlobalOptions.Server),
                Database: parseResult.GetValue(GlobalOptions.Database),
                Auth: GlobalOptions.AuthValue(parseResult),
                Query: query,
                Parameters: parameters.Count > 0 ? parameters : null,
                Limit: parseResult.GetValue(limitOption),
                NoValidate: parseResult.GetValue(noValidateOption),
                Trace: traceEnabled,
                TracePath: rawTracePath,
                Plan: parseResult.GetValue(planOption),
                Cold: parseResult.GetValue(coldOption),
                Runs: parseResult.GetValue(runsOption) ?? 1);

            // The raw-event dump reuses the shared trace-writer plumbing (file, or "-" for stderr).
            using var rawTraceWriter = TraceWriter.Open(rawTracePath, quiet);

            var result = await CliSpinner.RunAsync(
                "Running query...",
                () => new QueryModelHandler(_providers).HandleAsync(request, rawTraceWriter, cancellationToken),
                suppress: quiet || OutputFormats.IsJson(format) || OutputFormats.IsCsv(format) || rawTracePath == "-");

            // Timings/plans/benchmark are diagnostics (stderr); they are embedded in the result when
            // the primary sink is JSON, so avoid duplicating them there.
            var emitsJson = OutputFormats.IsJson(format)
                || (fileFormat is not null && OutputFormats.IsJson(fileFormat));

            int exitCode;
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
                exitCode = result.ExitCode;
            }
            else
            {
                exitCode = CommandOutput.Render(
                    result,
                    format,
                    data => QueryResultRenderer.Render(data, quiet),
                    data => data,
                    renderCsv: QueryResultRenderer.RenderCsv,
                    errorFormat: errorFormat);
            }

            if (!quiet && !emitsJson && result.Success && result.Data is { } data)
            {
                QueryResultRenderer.WriteTimings(data.Timings);
                QueryResultRenderer.WritePlans(data.Plans);
                QueryResultRenderer.WriteBenchmark(data.Benchmark);
            }

            return exitCode;
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
