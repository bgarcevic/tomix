using System.CommandLine;
using Tomix.Cli.Commands;

namespace Tomix.Cli.Tests;

/// <summary>
/// Issue #218 audit: <c>-q</c> belongs to the global <c>--quiet</c> flag on every command, so it
/// keeps exactly one meaning per command: no command may shadow it with a local option, and only
/// <c>query</c>/<c>get</c> document the swallowed-text collision. A new local <c>-q</c> or a new
/// collision rule must be a deliberate edit to these pins, not an accident.
/// </summary>
public sealed class QuietAliasAuditTests
{
    private static readonly HashSet<string> CommandsWithCollisionRule = new(StringComparer.Ordinal) { "query", "get" };

    [Fact]
    public void NoCommand_DeclaresALocalQAlias()
    {
        var offenders = TestRoot.Descendants(TestRoot.Full(), includeHidden: true)
            .Where(d => d.Command.Options.Any(o => o.Name == "-q" || o.Aliases.Contains("-q")))
            .Select(d => $"tx {string.Join(' ', d.Path)}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "These commands declare a local '-q' that shadows the global --quiet alias; add them to " +
            $"QuietAliasAuditTests deliberately if intended: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void QuietFollowedByPositionalText_CollidesOnlyOnQueryAndGet()
    {
        var root = TestRoot.Full();
        var checkedCommands = new List<string>();

        foreach (var (command, path) in TestRoot.Descendants(root, includeHidden: true))
        {
            if (command.Arguments.Count == 0)
                continue;

            // <path> <fillers for required positionals> -q y: on query/get the trailing text is
            // exactly the swallowed shape; everywhere else the guard must stay silent.
            var args = new List<string>(path);
            foreach (var argument in command.Arguments)
            {
                if (argument.Arity.MinimumNumberOfValues > 0)
                    args.Add("x");
            }
            args.Add("-q");
            args.Add("y");

            var collision = QuietCollisionGuard.FindCollision(root.Parse([.. args]));
            var commandKey = string.Join(' ', path);
            checkedCommands.Add(commandKey);

            Assert.True(collision is null || CommandsWithCollisionRule.Contains(command.Name),
                $"-q followed by positional text collides on 'tx {commandKey}', which has no documented " +
                "collision rule; add it to QuietCollisionGuard and QuietAliasAuditTests deliberately.");
        }

        // The sweep must stay meaningful: a refactor that empties the argument inventory would
        // otherwise silence this audit without anyone noticing.
        Assert.True(checkedCommands.Count >= 10,
            $"Expected to audit at least 10 commands with positional arguments, saw {checkedCommands.Count}.");
    }
}
