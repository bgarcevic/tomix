using System.CommandLine;
using Tomix.Cli.Output;
using Tomix.Core.Diagnostics;

namespace Tomix.Cli.Commands;

/// <summary>
/// The global <c>-q</c>/<c>--quiet</c> flag never consumes a value, so on the two commands whose
/// text users historically passed as <c>-q &lt;text&gt;</c> — <c>tx query</c> and
/// <c>tx get &lt;path&gt; --query &lt;property&gt;</c> — that text silently binds to a positional
/// argument instead (the query text, or get's optional [model] path) and the command misreads the
/// invocation. This guard rejects exactly that shape: a quiet token immediately followed by the
/// swallowed positional's token. A bare <c>--</c> before the text breaks the adjacency, so the
/// escape hatch still works.
/// </summary>
internal static class QuietCollisionGuard
{
    private sealed record CollisionRule(
        string ArgumentName,
        bool RequirePositionalBeforeQuiet,
        string MessageTemplate,
        string Hint);

    /// <summary>
    /// Per command: the positional that swallows text after <c>-q</c>, and whether the collision
    /// additionally requires another positional to have been satisfied before the flag (get's
    /// <c>[model]</c> only collides in <c>tx get &lt;path&gt; -q &lt;property&gt;</c>; a leading
    /// <c>-q</c> before both positionals binds them unambiguously and stays quiet usage).
    /// Pinned by <c>QuietAliasAuditTests</c>.
    /// </summary>
    private static readonly Dictionary<string, CollisionRule> Rules = new(StringComparer.Ordinal)
    {
        ["query"] = new(
            "query",
            RequirePositionalBeforeQuiet: false,
            "'-q' means --quiet; query text is not read from -q (got '{0}' right after it).",
            "Pass the query positionally: tx query \"EVALUATE ...\", or use --query, --file, or pipe it on stdin."),
        ["get"] = new(
            "model",
            RequirePositionalBeforeQuiet: true,
            "'-q' means --quiet; '{0}' was read as the optional [model] path, not as a query property.",
            "Read a single property with --query: tx get <path> --query <property>."),
    };

    /// <summary>
    /// Writes the collision diagnostic to stderr and returns true when the parsed invocation is
    /// the swallowed-text shape. Callers should exit with code 2.
    /// </summary>
    public static bool TryReject(ParseResult parseResult)
    {
        var collision = FindCollision(parseResult);
        if (collision is null)
            return false;

        ErrorOutput.Write([collision], GlobalOptions.ErrorFormatValue(parseResult));
        return true;
    }

    /// <summary>
    /// Token texts that turn on the global quiet flag. The recursive option's result does not
    /// carry the raw token on subcommands, so adjacency is matched against its known aliases.
    /// </summary>
    private static readonly HashSet<string> QuietTokenValues = GlobalOptions.Quiet.Aliases
        .Append(GlobalOptions.Quiet.Name)
        .Select(alias => alias.StartsWith('-') ? alias : $"--{alias}")
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>The named collision, or null when the invocation is not the swallowed-text shape.</summary>
    public static TomixDiagnostic? FindCollision(ParseResult parseResult)
    {
        var command = parseResult.CommandResult.Command;
        if (!Rules.TryGetValue(command.Name, out var rule))
            return null;
        if (!parseResult.GetValue(GlobalOptions.Quiet))
            return null;

        var swallowed = command.Arguments.FirstOrDefault(argument => argument.Name == rule.ArgumentName);
        var swallowedTokens = swallowed is null ? [] : parseResult.GetResult(swallowed)?.Tokens ?? [];
        if (swallowedTokens.Count == 0)
            return null;

        var swallowedValues = swallowedTokens.Select(token => token.Value).ToHashSet(StringComparer.Ordinal);
        HashSet<string>? precedingValues = null;
        if (rule.RequirePositionalBeforeQuiet)
        {
            var preceding = command.Arguments.FirstOrDefault(argument => !ReferenceEquals(argument, swallowed));
            precedingValues = preceding is null
                ? null
                : parseResult.GetResult(preceding)?.Tokens.Select(token => token.Value).ToHashSet(StringComparer.Ordinal);
        }

        var satisfied = !rule.RequirePositionalBeforeQuiet;
        string? previous = null;
        foreach (var token in parseResult.Tokens)
        {
            var value = token.Value;
            if (previous is not null
                && QuietTokenValues.Contains(previous)
                && swallowedValues.Contains(value)
                && satisfied)
                return new TomixDiagnostic(
                    "TOMIX_QUIET_COLLISION",
                    DiagnosticSeverity.Error,
                    string.Format(rule.MessageTemplate, value),
                    Hint: rule.Hint);

            if (precedingValues is not null && precedingValues.Contains(value))
                satisfied = true;

            previous = value;
        }

        return null;
    }
}
