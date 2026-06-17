using System.CommandLine;
using Tomix.App.Find;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Models;
using Spectre.Console;

namespace Tomix.Cli.Commands;

internal sealed class FindCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public FindCommand(IReadOnlyList<IModelProvider> providers) => _providers = providers;

    public Command Build()
    {
        var patternArgument = new Argument<string>("pattern")
        {
            Description = "Text or regex pattern to search for"
        };

        var modelArgument = new Argument<string>("model")
        {
            Description = "Path to model (if not using --model)",
            Arity = ArgumentArity.ZeroOrOne
        };

        var inOption = new Option<string?>("--in")
        {
            Description = "Scope: names, expressions, descriptions, displayFolders, formatStrings, annotations, all (default: all)"
        };
        var regexOption = new Option<bool>("--regex")
        {
            Description = "Treat pattern as a regular expression"
        };
        var caseSensitiveOption = new Option<bool>("--case-sensitive")
        {
            Description = "Enable case-sensitive matching"
        };
        var pathsOnlyOption = new Option<bool>("--paths-only")
        {
            Description = "Output one matching object path per line, suitable for piping"
        };
        var noMultilineOption = new Option<bool>("--no-multiline")
        {
            Description = "Collapse multi-line match context to a single line. Text output only."
        };

        var command = new Command("find", "Search for text across model objects")
        {
            patternArgument,
            modelArgument,
            inOption,
            regexOption,
            caseSensitiveOption,
            pathsOnlyOption,
            noMultilineOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var pattern = parseResult.GetValue(patternArgument) ?? "";
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            var errorFormat = parseResult.GetValue(GlobalOptions.ErrorFormat);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);

            if (!CommandOutput.TryValidateFormat(formatValue))
                return 2;

            var reference = new ActiveModelResolver().ResolveReference(
                GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument),
                parseResult.GetValue(GlobalOptions.Database));
            var result = await CliSpinner.RunAsync(
                "Searching...",
                () => new FindModelHandler(_providers).HandleAsync(
                    new FindModelRequest(
                        reference,
                        pattern,
                        parseResult.GetValue(inOption) ?? "all",
                        parseResult.GetValue(regexOption),
                        parseResult.GetValue(caseSensitiveOption)),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(formatValue));

            var pathsOnly = parseResult.GetValue(pathsOnlyOption);
            var noMultiline = parseResult.GetValue(noMultilineOption);
            return CommandOutput.Render(
                result,
                formatValue,
                data => Render(data, pathsOnly, noMultiline),
                ToReferenceJson,
                errorFormat: errorFormat);
        });

        return command;
    }

    private static void Render(FindModelResult result, bool pathsOnly, bool noMultiline)
    {
        if (result.Matches.Count == 0)
        {
            if (!pathsOnly)
            {
                AnsiConsole.MarkupLine(Styling.Muted($"No matches for '{Styling.MarkupEscape(result.Pattern)}'."));
                AnsiConsole.MarkupLine(Styling.Guidance($"  → Try: tx find \"{Styling.MarkupEscape(result.Pattern)}\" --type measure, or remove --case-sensitive"));
            }

            return;
        }

        if (pathsOnly)
        {
            foreach (var path in result.Matches.Select(m => m.Path).Distinct(StringComparer.OrdinalIgnoreCase))
                AnsiConsole.WriteLine(path);

            return;
        }

        _ = noMultiline;
        RenderMatchTable(result.Matches);
    }

    private static void RenderMatchTable(IReadOnlyList<FindMatch> matches)
    {
        var table = Styling.NewTable("Path", "Type", "Property", "Match", "Line");

        foreach (var match in matches)
            table.AddRow(
                Styling.MarkupEscape(match.Path),
                Styling.MarkupEscape(match.Type),
                Styling.MarkupEscape(match.Property),
                Styling.MarkupEscape(match.MatchedText),
                match.Line.ToString());

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(Styling.Muted($"{matches.Count} match(es)"));
    }

    private static object ToReferenceJson(FindModelResult result)
        => new
        {
            pattern = result.Pattern,
            matchCount = result.Matches.Count,
            matches = result.Matches.Select(match => new
            {
                objectPath = match.Path,
                objectType = match.Type,
                property = match.Property,
                matchedText = match.MatchedText,
                line = match.Line,
                position = match.Position
            })
        };
}
