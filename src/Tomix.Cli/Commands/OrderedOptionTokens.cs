using System.CommandLine;
using System.CommandLine.Parsing;

namespace Tomix.Cli.Commands;

/// <summary>
/// Reads options off <see cref="ParseResult.Tokens"/> in command-line order. Needed because
/// <c>System.CommandLine</c> exposes each repeated <see cref="Option{T}"/> as its own unordered array
/// via <c>GetValue</c>, which loses the interleaving between distinct options (e.g. which <c>-i</c>
/// pairs with which preceding <c>-q</c>). The token stream preserves input order and already
/// normalizes the <c>=</c>/<c>:</c>/space delimiters, so an option that takes a value appears as an
/// <see cref="TokenType.Option"/> token immediately followed by its <see cref="TokenType.Argument"/>.
/// </summary>
internal static class OrderedOptionTokens
{
    /// <summary>
    /// Yields <c>(option, value)</c> for every option token in order, with <c>value</c> set to the
    /// following argument token (or <c>null</c> for a flag with no argument). Option names are reported
    /// as typed, so callers match against every accepted alias (e.g. both <c>-S</c> and <c>--script</c>) —
    /// the token retains the alias the user wrote, not the canonical name.
    /// </summary>
    public static IEnumerable<(string Option, string? Value)> ReadOptions(ParseResult parseResult)
    {
        var tokens = parseResult.Tokens;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Type != TokenType.Option)
                continue;
            var value = i + 1 < tokens.Count && tokens[i + 1].Type == TokenType.Argument
                ? tokens[i + 1].Value
                : null;
            yield return (tokens[i].Value, value);
        }
    }
}
