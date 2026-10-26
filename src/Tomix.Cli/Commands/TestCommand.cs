using System.CommandLine;
using Tomix.App.State;
using Tomix.App.Test;
using Tomix.Cli.Output;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

/// <summary>
/// Runs DAX regression tests (paired <c>.dax</c> + <c>.expected.json</c> files) against a live
/// model and exits non-zero on any mismatch so CI can block a merge. Thin CLI shell over
/// <see cref="TestRunHandler"/>; mirrors <see cref="QueryCommand"/> for execution options and
/// <see cref="ValidateCommand"/> for the <c>--trx</c>/<c>--ci</c> pipeline outputs.
/// </summary>
internal sealed class TestCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    private readonly CliStateStore _state;

    private readonly Func<CliConnectionState?> _loadCurrentSession;

    public TestCommand(IReadOnlyList<IModelProvider> providers, CliStateStore state, Func<CliConnectionState?> loadCurrentSession)
    {
        _providers = providers;
        _state = state;
        _loadCurrentSession = loadCurrentSession;
    }

    public Command Build()
    {
        var pathArgument = new Argument<string>("path")
        {
            Description = "Test file or directory searched recursively for .dax tests (default: current directory)",
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = _ => "."
        };

        var updateOption = new Option<bool>("--update")
        {
            Description = "Record mode: run each query and (re)write its .expected.json snapshot from the actual result, listing what changed"
        };

        var updateMissingOption = new Option<bool>("--update-missing")
        {
            Description = "Record snapshots only for tests that have none yet; compare all other tests as usual"
        };

        var filterOption = new Option<string?>("--filter")
        {
            Description = "Run only tests whose name matches a * wildcard pattern (case-insensitive)"
        };

        var paramOption = new Option<string[]>("--param")
        {
            Description = "Query parameter as name=value, referenced as @name in DAX. Applied to every test. Repeatable.",
            Arity = ArgumentArity.ZeroOrMore
        };

        var maxRowsOption = new Option<int?>("--max-rows")
        {
            Description = "Per-query row cap; a query exceeding it fails as an error (default: 10000)"
        };
        maxRowsOption.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int?>() is < 1)
                result.AddError("--max-rows must be at least 1.");
        });

        var toleranceOption = new Option<string?>("--tolerance")
        {
            Description = "Accept numeric cells within this difference: absolute (0.01) or relative to the larger value (0.1%)"
        };
        toleranceOption.Validators.Add(result =>
        {
            if (!TestTolerance.TryParse(result.GetValueOrDefault<string?>(), out _, out var error))
                result.AddError(error!);
        });

        var ignoreRowOrderOption = new Option<bool>("--ignore-row-order")
        {
            Description = "Sort rows by their values before comparing, so queries need no ORDER BY"
        };

        var ciOption = new Option<string?>("--ci")
        {
            Description = "Print CI log-group commands to stderr for the given system: vsts or github"
        };

        var trxOption = new Option<string?>("--trx")
        {
            Description = "Write results to a .trx test-run file at this path"
        };

        var baselineOption = new Option<string?>("--baseline")
        {
            Description = "Compare with the live model of this saved profile instead of .expected.json snapshots"
        };

        var baselineServerOption = new Option<string?>("--baseline-server")
        {
            Description = "Compare with a live model on this server or workspace instead of snapshots (default: the tested model's server)"
        };

        var baselineDatabaseOption = new Option<string?>("--baseline-database")
        {
            Description = "Database of the baseline model (default: the profile's, or the tested model's)"
        };

        var command = new Command("test", "Run DAX regression tests against a live model")
        {
            pathArgument,
            updateOption,
            updateMissingOption,
            filterOption,
            paramOption,
            maxRowsOption,
            toleranceOption,
            ignoreRowOrderOption,
            ciOption,
            trxOption,
            baselineOption,
            baselineServerOption,
            baselineDatabaseOption
        };

        command.Validators.Add(result =>
        {
            if (result.GetValue(updateOption) && result.GetValue(updateMissingOption))
                result.AddError("--update and --update-missing cannot be combined; choose one.");

            var hasBaseline = !string.IsNullOrWhiteSpace(result.GetValue(baselineOption))
                || !string.IsNullOrWhiteSpace(result.GetValue(baselineServerOption))
                || !string.IsNullOrWhiteSpace(result.GetValue(baselineDatabaseOption));
            if (hasBaseline && (result.GetValue(updateOption) || result.GetValue(updateMissingOption)))
                result.AddError("--update and --update-missing cannot be combined with a baseline model; a baseline run reads and writes no snapshots.");

            if (!string.IsNullOrWhiteSpace(result.GetValue(baselineOption))
                && !string.IsNullOrWhiteSpace(result.GetValue(baselineServerOption)))
                result.AddError("--baseline and --baseline-server cannot be combined; the profile names the baseline server.");
        });

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            var errorFormat = GlobalOptions.ErrorFormatValue(parseResult, format);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            if (!CommandOutput.TryValidateFormat(parseResult, format, "test", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var parameters = QueryCommand.ParseParams(parseResult.GetValue(paramOption), out var badParam);
            if (parameters is null)
            {
                ErrorOutput.Write(
                    new[]
                    {
                        new TomixDiagnostic(
                            "TOMIX_TEST_BAD_PARAM",
                            DiagnosticSeverity.Error,
                            $"Invalid --param value '{badParam}'. Expected name=value.",
                            Hint: "Example: --param color=Red (referenced as @color in the query)")
                    },
                    errorFormat);
                return 2;
            }

            // A profile is resolved once, before any model opens, like deploy --profile.
            var baselineProfile = parseResult.GetValue(baselineOption);
            var baselineServer = parseResult.GetValue(baselineServerOption);
            var baselineDatabase = parseResult.GetValue(baselineDatabaseOption);
            if (!string.IsNullOrWhiteSpace(baselineProfile))
            {
                var resolved = TestBaselineProfileResolver.Resolve(_state, baselineProfile);
                if (!resolved.Success)
                    return CommandOutput.Render(resolved, format, errorFormat, _ => { });

                baselineServer = resolved.Data!.Server;
                if (string.IsNullOrWhiteSpace(baselineDatabase))
                    baselineDatabase = resolved.Data.Database;
            }

            var request = new TestRunRequest(
                Model: GlobalOptions.ModelValue(parseResult),
                Server: parseResult.GetValue(GlobalOptions.Server),
                Database: parseResult.GetValue(GlobalOptions.Database),
                Auth: GlobalOptions.AuthValue(parseResult),
                Path: parseResult.GetValue(pathArgument)!,
                Update: parseResult.GetValue(updateOption),
                Filter: parseResult.GetValue(filterOption),
                Parameters: parameters.Count > 0 ? parameters : null,
                MaxRows: parseResult.GetValue(maxRowsOption) ?? 10000,
                Compare: CompareOptions(parseResult.GetValue(toleranceOption), parseResult.GetValue(ignoreRowOrderOption)),
                UpdateMissing: parseResult.GetValue(updateMissingOption),
                BaselineServer: baselineServer,
                BaselineDatabase: baselineDatabase,
                BaselineProfile: string.IsNullOrWhiteSpace(baselineProfile) ? null : baselineProfile);

            // Name the session-resolved target on stderr before the model opens; an explicit
            // model/--server target is the user's own and stays silent.
            ConnectionBanner.AnnounceIfImplicit(
                parseResult,
                request.Model,
                () => new ActiveModelResolver(_loadCurrentSession)
                    .ResolveReference(request.Model, request.Database, request.Server),
                _loadCurrentSession);

            var result = await CliSpinner.RunAsync(
                "Running tests...",
                () => new TestRunHandler(_providers, _loadCurrentSession).HandleAsync(request, cancellationToken),
                suppress: quiet || OutputFormats.IsJson(format));

            if (result.Data is not null)
            {
                var trx = parseResult.GetValue(trxOption);
                if (!string.IsNullOrWhiteSpace(trx))
                    TrxWriter.Write(trx, "tx test", TestRunRenderer.ToTrxTests(result.Data));

                TestRunRenderer.EmitCi(parseResult.GetValue(ciOption), result.Data);
            }

            return CommandOutput.Render(
                result,
                format,
                data => TestRunRenderer.Render(data, quiet),
                data => data,
                renderCsv: null,
                errorFormat: errorFormat);
        });

        return command;
    }

    // The option validator has already rejected a malformed tolerance; zero means exact.
    private static TestCompareOptions CompareOptions(string? tolerance, bool ignoreRowOrder)
        => new(
            Tolerance: TestTolerance.TryParse(tolerance, out var parsed, out _) && parsed!.Value > 0 ? parsed : null,
            IgnoreRowOrder: ignoreRowOrder);
}
