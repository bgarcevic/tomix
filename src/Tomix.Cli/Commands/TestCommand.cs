using System.CommandLine;
using Tomix.App;
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

    private readonly AppServices _services;

    public TestCommand(IReadOnlyList<IModelProvider> providers, AppServices services)
    {
        _providers = providers;
        _services = services;
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
            Description = "Record mode: run each query and (re)write its .expected.json snapshot from the actual result."
        };

        var filterOption = new Option<string?>("--filter")
        {
            Description = "Run only tests whose name matches a * wildcard pattern (case-insensitive)."
        };

        var paramOption = new Option<string[]>("--param")
        {
            Description = "Query parameter as name=value, referenced as @name in DAX. Applied to every test. Repeatable.",
            Arity = ArgumentArity.ZeroOrMore
        };

        var maxRowsOption = new Option<int?>("--max-rows")
        {
            Description = "Per-query row cap; a query exceeding it fails as an error (default: 10000)."
        };
        maxRowsOption.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int?>() is < 1)
                result.AddError("--max-rows must be at least 1.");
        });

        var ciOption = new Option<string?>("--ci")
        {
            Description = "Emit CI logging commands to stderr: vsts or github"
        };

        var trxOption = new Option<string?>("--trx")
        {
            Description = "Write results as a VSTEST .trx file to the specified path"
        };

        var command = new Command("test", "Run DAX regression tests against a live model (--update records snapshots, --trx/--ci for pipelines)")
        {
            pathArgument,
            updateOption,
            filterOption,
            paramOption,
            maxRowsOption,
            ciOption,
            trxOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            var errorFormat = parseResult.GetValue(GlobalOptions.ErrorFormat);
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

            var request = new TestRunRequest(
                Model: GlobalOptions.ModelValue(parseResult),
                Server: parseResult.GetValue(GlobalOptions.Server),
                Database: parseResult.GetValue(GlobalOptions.Database),
                Auth: GlobalOptions.AuthValue(parseResult),
                Path: parseResult.GetValue(pathArgument)!,
                Update: parseResult.GetValue(updateOption),
                Filter: parseResult.GetValue(filterOption),
                Parameters: parameters.Count > 0 ? parameters : null,
                MaxRows: parseResult.GetValue(maxRowsOption) ?? 10000);

            var result = await CliSpinner.RunAsync(
                "Running tests...",
                () => new TestRunHandler(_providers, _services.LoadCurrentSession).HandleAsync(request, cancellationToken),
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
}
