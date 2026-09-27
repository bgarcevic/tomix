using System.CommandLine;
using System.CommandLine.Invocation;
using Tomix.Cli.Output;

namespace Tomix.Cli.Commands;

/// <summary>
/// Reports a parse that System.CommandLine rejected, with at most one suggestion: the command's
/// own options for an option typo, or the parent's subcommands for a command typo.
/// </summary>
internal static class UsageErrors
{
    /// <summary>Writes the usage error to stderr and returns exit code 2.</summary>
    public static int Report(ParseResult parseResult, IReadOnlyList<string> args)
    {
        // An option typo is answered with the command's options, whether the token was bound to
        // a positional or left unmatched. Its error replaces the library's for the same token.
        if (UnknownOptionGuard.TryReject(parseResult, args))
            return 2;

        // The library's corrections run once per unmatched token and compare arguments against
        // every command and option name, so a model path gets a list of unrelated commands.
        if (parseResult.Action is ParseErrorAction action)
            action.ShowTypoCorrections = false;

        WriteCommandSuggestion(parseResult);

        // Invoke prints the parse errors, but returns System.CommandLine's default of 1;
        // usage errors exit 2 per the documented contract (docs/error-codes.md).
        parseResult.Invoke();
        return 2;
    }

    // Only a command group can have a mistyped subcommand; the matched command is never a
    // candidate, so "tx set Foo bar baz" cannot suggest "set".
    private static void WriteCommandSuggestion(ParseResult parseResult)
    {
        var command = parseResult.CommandResult.Command;
        var typo = parseResult.UnmatchedTokens.FirstOrDefault(t => !t.StartsWith('-'));
        if (typo is null || command.Subcommands.Count == 0)
            return;

        var names = command.Subcommands
            .Where(c => !c.Hidden)
            .SelectMany(c => c.Aliases.Prepend(c.Name))
            .ToList();
        DidYouMean.WriteSuggestion(typo, names);
    }
}
