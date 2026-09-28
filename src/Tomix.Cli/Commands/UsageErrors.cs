using System.CommandLine;
using Tomix.Cli.Output;
using Tomix.Core.Diagnostics;

namespace Tomix.Cli.Commands;

/// <summary>
/// Reports a parse that System.CommandLine rejected as one diagnostic on stderr, with at most one
/// suggestion: the command's own options for an option typo, or the parent's subcommands for a
/// command typo. The library's own error action is never run: it printed its messages in an
/// arbitrary order and then the whole help page to stdout, so a typo produced sixty lines.
/// </summary>
internal static class UsageErrors
{
    /// <summary>
    /// Writes the usage error to stderr and returns exit code 2 — or, for a bare command group
    /// such as <c>tx bpa</c>, writes that group's help and returns 0.
    /// </summary>
    public static int Report(ParseResult parseResult, IReadOnlyList<string> args)
    {
        // An option typo is answered with the command's options, whether the token was bound to
        // a positional or left unmatched. Its error replaces the library's for the same token.
        if (UnknownOptionGuard.TryReject(parseResult, args))
            return 2;

        var command = parseResult.CommandResult.Command;
        var hasSubcommands = command.Subcommands.Any(c => !c.Hidden);
        var typo = parseResult.UnmatchedTokens.FirstOrDefault(t => !t.StartsWith('-'));

        // A bare group is a request for its commands, not an error (docs/cli-ux-guidelines.md).
        if (hasSubcommands && typo is null && command.Action is null)
        {
            SpectreHelpAction.Write(command, concise: false, SpectreHelpAction.TerminalWidth());
            return 0;
        }

        var help = HelpCommand(command);
        var diagnostic = hasSubcommands && typo is not null
            ? UnknownCommand(command, typo, help)
            : new TomixDiagnostic(
                "TOMIX_USAGE",
                DiagnosticSeverity.Error,
                Message(parseResult),
                $"Run '{help}' for usage.");

        ErrorOutput.Write([diagnostic], GlobalOptions.ErrorFormatValue(parseResult));
        return 2;
    }

    /// <summary>The help invocation for a command: <c>tx bpa run --help</c>.</summary>
    internal static string HelpCommand(Command command)
    {
        var path = SpectreHelpAction.CommandPath(command);
        return path.Length == 0 ? "tx --help" : $"tx {path} --help";
    }

    // Only a command group can have a mistyped subcommand; the matched command is never a
    // candidate, so "tx set Foo bar baz" cannot suggest "set".
    private static TomixDiagnostic UnknownCommand(Command command, string typo, string help)
    {
        var names = command.Subcommands
            .Where(c => !c.Hidden)
            .SelectMany(c => c.Aliases.Prepend(c.Name))
            .ToList();
        var suggestion = DidYouMean.Suggest(typo, names);
        var path = SpectreHelpAction.CommandPath(command);
        var scope = path.Length == 0 ? "" : $" for 'tx {path}'";
        var list = $"Run '{help}' to see the commands.";

        return new TomixDiagnostic(
            "TOMIX_UNKNOWN_COMMAND",
            DiagnosticSeverity.Error,
            $"Unknown command '{typo}'{scope}.",
            suggestion is null ? list : $"Did you mean '{suggestion}'? {list}");
    }

    /// <summary>
    /// The library's first error, reworded where its text is unclear. One message is enough: the
    /// rest are almost always consequences of the first.
    /// </summary>
    private static string Message(ParseResult parseResult)
    {
        var message = parseResult.Errors[0].Message.TrimEnd('.');
        const string unrecognized = "Unrecognized command or argument ";
        if (message.StartsWith(unrecognized, StringComparison.Ordinal))
            return $"Unexpected argument {message[unrecognized.Length..]}";
        return message;
    }
}
