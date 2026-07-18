using System.CommandLine;
using System.Text;
using Tomix.App.Format;
using Tomix.Cli.Commands;
using Tomix.Cli.Output;

namespace Tomix.Cli.Tests;

/// <summary>
/// The per-command help shows hand-maintained example invocations. These tests parse every
/// example against the real root command so an example cannot drift out of sync with the
/// options a command actually accepts (e.g. a renamed or removed flag).
/// </summary>
public sealed class HelpExampleParityTests
{
    private static RootCommand BuildRoot()
        => Program.BuildRootCommand(
            providers: [],
            new CompositeExpressionFormatterClient([]),
            version: "0.0.0-test",
            TestServices.Create());

    [Fact]
    public void EveryExampleKey_ResolvesToARegisteredCommand()
    {
        var root = BuildRoot();
        var stale = SpectreHelpAction.CommandExamples.Keys
            .Where(key => ResolveCommand(root, key) is null)
            .ToList();

        Assert.True(stale.Count == 0,
            $"CommandExamples keys with no matching registered command: {string.Join(", ", stale)}");
    }

    [Fact]
    public void EveryExample_ParsesWithoutErrors()
    {
        var root = BuildRoot();
        var failures = new List<string>();

        foreach (var example in SpectreHelpAction.CommandExamples.Values.SelectMany(e => e))
        {
            var tokens = Tokenize(example);
            if (tokens.Length == 0 || tokens[0] != "tx")
            {
                failures.Add($"'{example}': does not start with 'tx'");
                continue;
            }

            var args = tokens.Skip(1).ToArray();
            var parseResult = root.Parse(args);

            foreach (var error in parseResult.Errors)
                failures.Add($"'{example}': {error.Message}");

            // Unknown options bind silently to optional positional arguments instead of
            // producing parse errors; the CLI rejects them at runtime via UnknownOptionGuard,
            // so examples must pass the same check.
            var offending = UnknownOptionGuard.FindOffendingToken(parseResult, args);
            if (offending is not null)
                failures.Add($"'{example}': unrecognized option {offending}");
        }

        Assert.True(failures.Count == 0,
            "Help examples that are not valid invocations:\n" + string.Join("\n", failures));
    }

    private static Command? ResolveCommand(Command root, string key)
    {
        Command current = root;
        foreach (var name in key.Split(' '))
        {
            var next = current.Subcommands.FirstOrDefault(sc => sc.Name == name);
            if (next is null)
                return null;
            current = next;
        }

        return current;
    }

    /// <summary>
    /// Shell-style tokenizer for example strings: honors double quotes (with backslash-escaped
    /// quotes inside), and stops at unquoted shell operators (|, &lt;, &gt;) since everything
    /// after them is consumed by the shell, not the CLI.
    /// </summary>
    private static string[] Tokenize(string example)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var (c, i) in example.Select((c, i) => (c, i)))
        {
            if (inQuotes)
            {
                if (c == '"' && example[i - 1] != '\\')
                    inQuotes = false;
                else if (c != '\\' || i + 1 >= example.Length || example[i + 1] != '"')
                    current.Append(c);
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c is '|' or '<' or '>')
            {
                break;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens.ToArray();
    }
}
