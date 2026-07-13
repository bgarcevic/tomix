using System.CommandLine;
using Tomix.Cli.Output;
using Tomix.Core.Diagnostics;

namespace Tomix.Cli.Commands;

/// <summary>
/// Rejects option-lookalike tokens (e.g. a typo'd <c>--path-only</c>) that System.CommandLine
/// would otherwise bind to an optional positional argument, silently changing what the command
/// does. Tokens after a bare <c>--</c> separator are exempt so callers can still pass literal
/// values that start with a dash.
/// </summary>
internal static class UnknownOptionGuard
{
    /// <summary>
    /// Writes a usage error to stderr and returns true when an option-lookalike token was bound
    /// to a positional argument. Callers should exit with code 2.
    /// </summary>
    public static bool TryReject(ParseResult parseResult, IReadOnlyList<string> args)
    {
        var offending = FindOffendingToken(parseResult, args);
        if (offending is null)
            return false;

        ErrorOutput.Write(
            [new TomixDiagnostic(
                "TOMIX_UNKNOWN_OPTION",
                DiagnosticSeverity.Error,
                $"Unrecognized option: {offending}",
                Hint(offending, parseResult.CommandResult.Command))],
            parseResult.GetValue(GlobalOptions.ErrorFormat));
        return true;
    }

    public static string? FindOffendingToken(ParseResult parseResult, IReadOnlyList<string> args)
    {
        var lookalikes = args
            .TakeWhile(a => a != "--")
            .Where(a => a.Length > 1 && a[0] == '-')
            .ToHashSet(StringComparer.Ordinal);

        if (lookalikes.Count == 0)
            return null;

        foreach (var argument in parseResult.CommandResult.Command.Arguments)
        {
            var result = parseResult.GetResult(argument);
            if (result is null)
                continue;

            var offending = result.Tokens.FirstOrDefault(t => lookalikes.Contains(t.Value));
            if (offending is not null)
                return offending.Value;
        }

        return null;
    }

    private static string Hint(string token, Command command)
    {
        var known = command.Options.Concat(GlobalOptions.All())
            .SelectMany(o => o.Aliases.Prepend(o.Name))
            .Append("--help")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var escape = $"Run 'tx {command.Name} --help' to see options, or put '--' before " +
                     "positional values that start with '-'.";
        var suggestion = DidYouMean.Suggest(token, known);
        return suggestion is null ? escape : $"Did you mean '{suggestion}'? {escape}";
    }
}
