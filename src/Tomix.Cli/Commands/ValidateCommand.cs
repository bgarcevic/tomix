using System.CommandLine;
using System.Xml.Linq;
using Tomix.App.State;
using Tomix.App.Validate;
using Tomix.Cli.Output;
using Tomix.Core.Models;
using Spectre.Console;

namespace Tomix.Cli.Commands;

internal sealed class ValidateCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public ValidateCommand(IReadOnlyList<IModelProvider> providers) => _providers = providers;

    public Command Build()
    {
        var modelArgument = new Argument<string>("model")
        {
            Description = "Path to model (if not using --model)",
            Arity = ArgumentArity.ZeroOrOne
        };
        var ciOption = new Option<string?>("--ci")
        {
            Description = "Emit CI logging commands to stderr: vsts or github"
        };
        var trxOption = new Option<string?>("--trx")
        {
            Description = "Write results as a VSTEST .trx file to the specified path"
        };
        var errorsOnlyOption = new Option<bool>("--errors-only")
        {
            Description = "Only show errors"
        };
        var noWarningsOption = new Option<bool>("--no-warnings")
        {
            Description = "Hide warnings from the semantic analyzer"
        };
        var noMultilineOption = new Option<bool>("--no-multiline")
        {
            Description = "Collapse multi-line cell content to a single line. Text output only."
        };
        var serverOnlyOption = new Option<bool>("--server-only")
        {
            Description = "Only show errors reported by the connected server"
        };

        var command = new Command("validate", "Validate DAX expressions and relationship integrity (--ci for CI output, --trx for VSTEST)")
        {
            modelArgument,
            ciOption,
            trxOption,
            errorsOnlyOption,
            noWarningsOption,
            noMultilineOption,
            serverOnlyOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            if (!CommandOutput.TryValidateFormat(format, "validate", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var errorsOnly = parseResult.GetValue(errorsOnlyOption);

            if (!RecentConnections.TryGetSource(
                    parseResult,
                    GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument),
                    out var source,
                    out var recentExit))
                return recentExit;

            var result = await CliSpinner.RunAsync(
                "Validating model...",
                () => new ValidateModelHandler(_providers).HandleAsync(
                    new ValidateModelRequest(
                        RecentConnections.CreateResolver(source).ResolveReference(source.Model, source.Database, source.Server),
                        errorsOnly,
                        parseResult.GetValue(noWarningsOption) || errorsOnly,
                        parseResult.GetValue(serverOnlyOption)),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(format) || OutputFormats.IsCsv(format));

            if (result.Data is not null)
            {
                var trx = parseResult.GetValue(trxOption);
                if (!string.IsNullOrWhiteSpace(trx))
                    WriteTrx(trx, result.Data);

                EmitCi(parseResult.GetValue(ciOption), result.Data);
            }

            return CommandOutput.Render(
                result,
                format,
                data => Render(data, errorsOnly, parseResult.GetValue(noMultilineOption), includeBanner: !OutputFormats.IsCsv(format)));
        });

        return command;
    }

    private static void Render(
        ValidateModelResult result,
        bool errorsOnly,
        bool noMultiline,
        bool includeBanner)
    {
        if (includeBanner)
            AnsiConsole.MarkupLine(Styling.Value("Validating..."));
        AnsiConsole.MarkupLine(Styling.Muted("Validating: (unnamed)"));
        AnsiConsole.WriteLine();

        if (result.Valid)
        {
            AnsiConsole.MarkupLine(Styling.Success("No validation errors found."));
            if (errorsOnly || result.Warnings.Count == 0)
                return;
        }

        var allRows = new List<(string Kind, string Message, string Object, string Line)>();

        foreach (var error in result.Errors)
        {
            var message = noMultiline ? error.Message.ReplaceLineEndings(" ") : error.Message;
            allRows.Add(("Error", message, error.ObjectName, error.Expression ?? ""));
        }

        if (!errorsOnly)
        {
            foreach (var warning in result.Warnings)
            {
                var message = noMultiline ? warning.Message.ReplaceLineEndings(" ") : warning.Message;
                allRows.Add(("Warning", message, warning.ObjectName, warning.Expression ?? ""));
            }
        }

        var hasErrors = result.Errors.Count > 0;
        var hasWarnings = !errorsOnly && result.Warnings.Count > 0;

        if (hasErrors)
        {
            AnsiConsole.MarkupLine(Styling.Error("Errors"));
            RenderTable(allRows.Where(r => r.Kind == "Error").ToList());
        }

        if (hasWarnings)
        {
            AnsiConsole.MarkupLine(Styling.Warning("Warnings"));
            RenderTable(allRows.Where(r => r.Kind == "Warning").ToList());
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  {Styling.KeyValue("Errors:", result.Errors.Count.ToString())}");

        if (!errorsOnly)
        {
            AnsiConsole.MarkupLine($"  {Styling.KeyValue("Warnings:", result.Warnings.Count.ToString())}");
        }

        AnsiConsole.MarkupLine($"  {Styling.KeyValue("Anti-patterns:", "0")}");
    }

    private static void RenderTable(IReadOnlyList<(string Kind, string Message, string Object, string Line)> rows)
    {
        if (rows.Count == 0)
            return;

        var table = Styling.NewTable("Message", "Object", "Line");

        foreach (var row in rows)
            table.AddRow(
                Styling.MarkupEscape(row.Message),
                Styling.MarkupEscape(row.Object),
                Styling.MarkupEscape(row.Line));

        AnsiConsole.Write(table);
    }

    private static void EmitCi(string? ci, ValidateModelResult result)
    {
        if (string.IsNullOrWhiteSpace(ci) || result.Valid)
            return;

        if (ci.Equals("github", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var error in result.Errors)
                Console.Error.WriteLine($"::error::{error.Message} [{error.ObjectName}] ({error.Code})");
        }
        else if (ci.Equals("vsts", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var error in result.Errors)
                Console.Error.WriteLine($"##vso[task.logissue type=error;]{error.Message} [{error.ObjectName}] ({error.Code})");

            Console.Error.WriteLine("##vso[task.complete result=Failed;]Done.");
        }
    }

    private static void WriteTrx(string path, ValidateModelResult result)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var doc = new XDocument(
            new XElement("TestRun",
                new XAttribute("name", "tx validate"),
                new XElement("ResultSummary",
                    new XAttribute("outcome", result.Valid ? "Passed" : "Failed"))));
        doc.Save(fullPath);
    }
}
