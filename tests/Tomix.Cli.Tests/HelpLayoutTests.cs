using System.CommandLine;
using System.Text.RegularExpressions;
using Tomix.Cli.Commands;
using Tomix.Cli.Output;

namespace Tomix.Cli.Tests;

/// <summary>
/// The help layout contract. Help used to render at unlimited width, so a terminal hard-wrapped
/// it at column 0 mid-word; it listed thirteen global options on every page and all five help
/// aliases; and its placeholders repeated the option name (<c>--save-to &lt;save-to&gt;</c>).
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed partial class HelpLayoutTests
{
    public static TheoryData<int> Widths => [80, 100];

    [Theory]
    [MemberData(nameof(Widths))]
    public void EveryHelpPage_FitsTheTerminalWidth(int width)
    {
        var root = TestRoot.Full();
        var pages = TestRoot.Descendants(root, includeHidden: false)
            .Select(entry => (Name: string.Join(' ', entry.Path), entry.Command))
            .Prepend(("(root)", root));

        // Examples are copy-pasteable commands and are never wrapped, so they count too: an
        // example longer than the page is one the terminal breaks mid-command.
        var concise = Plain(ConsoleCapture.Run(
            () => SpectreHelpAction.Write(root, concise: true, width), captureAnsiConsole: true).Stdout);
        var overflows = new List<string>();
        var rendered = new List<(string Name, string Text)> { ("(bare tx)", concise) };
        foreach (var (name, command) in pages)
            rendered.Add((name ?? "", Render(command, width)));
        foreach (var (name, text) in rendered)
        {
            foreach (var line in text.Split('\n'))
            {
                if (line.TrimEnd().Length > width)
                    overflows.Add($"{name}: {line.TrimEnd()}");
            }
        }

        Assert.True(overflows.Count == 0, string.Join(Environment.NewLine, overflows));
    }

    [Fact]
    public void WrappedDescription_ContinuesUnderItsColumn()
    {
        var help = Render(Command("get"), 80);

        var lines = help.Split('\n');
        var start = Array.FindIndex(lines, l => l.Contains("-t, --type <type>", StringComparison.Ordinal));
        var column = lines[start].IndexOf("Object kind", StringComparison.Ordinal);

        Assert.True(column > 0);
        Assert.Equal(new string(' ', column), lines[start + 1][..column]);
        Assert.NotEqual(' ', lines[start + 1][column]);
    }

    [Fact]
    public void RedirectedHelp_IsNotWrapped()
    {
        var help = Render(Command("get"), int.MaxValue);

        Assert.Contains(
            "Object kind: picks one when a path matches several objects, or filters a list; for example table,",
            help);
    }

    [Fact]
    public void RootHelp_ListsCommandsBeforeGlobalOptions()
    {
        var help = Render(TestRoot.Full(), 100);

        Assert.True(help.IndexOf("DISCOVER", StringComparison.Ordinal)
                    < help.IndexOf("GLOBAL OPTIONS", StringComparison.Ordinal));
        Assert.Contains("  -h, --help", help);
        Assert.DoesNotContain("/?", help);
        Assert.Contains("-m, --model <path>", help);
        Assert.Contains(SpectreHelpAction.DocsUrl, help);
    }

    [Fact]
    public void BareRoot_IsConcise()
    {
        var captured = ConsoleCapture.Run(
            () => SpectreHelpAction.Write(TestRoot.Full(), concise: true, 100), captureAnsiConsole: true);

        var help = Plain(captured.Stdout);
        Assert.Contains("DISCOVER", help);
        Assert.DoesNotContain("GLOBAL OPTIONS", help);
        Assert.Contains("tx --help", help);
    }

    [Fact]
    public void CommandHelp_NamesGlobalsOnOneLine_InsteadOfListingThem()
    {
        var help = Render(Command("ls"), 100);

        Assert.Matches(@"(?m)^GLOBAL OPTIONS\r?\n  --help, --model", help);
        Assert.DoesNotContain("Path to the semantic model", help);
        Assert.DoesNotContain("Description:", help);
    }

    [Fact]
    public void MutationHelp_GroupsSaveFlagsLast()
    {
        var help = Render(Command("add"), 100);

        var options = help.IndexOf("\nOPTIONS", StringComparison.Ordinal);
        var partition = help.IndexOf("\nPARTITION AND DATA SOURCE OPTIONS", StringComparison.Ordinal);
        var save = help.IndexOf("\nSAVE OPTIONS", StringComparison.Ordinal);
        Assert.True(options >= 0 && options < partition && partition < save, help);
        Assert.Contains("--save-to <path>", help[save..]);
    }

    [Fact]
    public void EveryValueOption_HasAPlaceholderName()
    {
        var missing = new List<string>();
        foreach (var (command, path) in TestRoot.Descendants(TestRoot.Full(), includeHidden: false))
        {
            foreach (var option in command.Options.Where(o => !o.Hidden && HelpPlaceholders.TakesValue(o)))
            {
                if (option.HelpName is null)
                    missing.Add($"tx {string.Join(' ', path)} {option.Name}");
            }
        }

        Assert.True(missing.Count == 0,
            "Options without a help placeholder — add them to HelpPlaceholders.Names:" + Environment.NewLine +
            string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void CommandDescriptions_AreShortPlainOneLiners()
    {
        var offenders = TestRoot.Descendants(TestRoot.Full(), includeHidden: false)
            .Select(entry => (Path: string.Join(' ', entry.Path), Text: entry.Command.Description ?? ""))
            .Where(d => d.Text.Length is 0 or > 60 || d.Text.EndsWith('.') || d.Text.Contains("--", StringComparison.Ordinal))
            .Select(d => $"tx {d.Path}: \"{d.Text}\"")
            .ToList();

        Assert.True(offenders.Count == 0,
            "Command descriptions must be at most 60 characters, with no trailing period and no flags " +
            "(put detail in SpectreHelpAction.CommandNotes):" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void OptionDescriptions_FollowOnePunctuationAndDefaultStyle()
    {
        var offenders = new List<string>();
        var root = TestRoot.Full();
        var symbols = TestRoot.Descendants(root, includeHidden: false)
            .SelectMany(entry => entry.Command.Options.Where(o => !o.Hidden).Select(o => (entry.Path, (Symbol)o))
                .Concat(entry.Command.Arguments.Where(a => !a.Hidden).Select(a => (entry.Path, (Symbol)a))))
            .Concat(root.Options.Select(o => (Path: Array.Empty<string>(), (Symbol)o)));

        foreach (var (path, symbol) in symbols)
        {
            var text = symbol.Description ?? "";
            var multiSentence = SentenceBreak().IsMatch(text);
            var problem =
                text.Contains("(default)", StringComparison.Ordinal) ? "write defaults as '(default: x)'" :
                DefaultsSentence().IsMatch(text) ? "write defaults as '(default: x)'" :
                !multiSentence && text.EndsWith('.') ? "one sentence: no trailing period" :
                multiSentence && !text.EndsWith('.') ? "several sentences: end with a period" :
                null;
            if (problem is not null)
                offenders.Add($"tx {string.Join(' ', path)} {symbol.Name}: {problem}: \"{text}\"");
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Theory]
    [InlineData(new[] { "help" }, new[] { "--help" })]
    [InlineData(new[] { "help", "bpa", "run" }, new[] { "bpa", "run", "--help" })]
    [InlineData(new[] { "ls", "help" }, new[] { "ls", "help" })]
    public void HelpCommand_IsRewrittenToTheHelpOption(string[] args, string[] expected)
        => Assert.Equal(expected, Program.RewriteHelpCommand(args));

    [Fact]
    public void HelpCommand_RendersTheCommandsPage()
    {
        var parsed = TestRoot.Full().Parse(Program.RewriteHelpCommand(["help", "bpa", "run"]));

        var captured = ConsoleCapture.Invoke(parsed, captureAnsiConsole: true);

        Assert.Equal(0, captured.ExitCode);
        Assert.Matches(@"(?m)^USAGE\r?\n  tx bpa run \[model\] \[options\]", Plain(captured.Stdout));
    }

    [Fact]
    public void UnknownCommand_IsOneDiagnosticOnStderr_WithNoHelpDump()
    {
        var captured = Report("lss");

        Assert.Equal(2, captured.ExitCode);
        Assert.Equal("", captured.Stdout);
        Assert.Contains("Unknown command 'lss'.", captured.Stderr);
        Assert.Contains("Did you mean 'ls'?", captured.Stderr);
        Assert.DoesNotContain("Required command", captured.Stderr);
    }

    [Fact]
    public void UnknownCommand_CarriesCodeInJson()
    {
        var captured = Report("lss", "--error-format", "json");

        var error = System.Text.Json.JsonDocument.Parse(captured.Stderr).RootElement;
        Assert.Equal("TOMIX_UNKNOWN_COMMAND", error.GetProperty("code").GetString());
    }

    [Fact]
    public void MissingArgument_IsAUsageDiagnostic_PointingAtTheCommandsHelp()
    {
        var captured = Report("bpa", "rules", "show");

        Assert.Equal(2, captured.ExitCode);
        Assert.Equal("", captured.Stdout);
        Assert.Contains("Run 'tx bpa rules show --help' for usage.", captured.Stderr);
    }

    [Fact]
    public void BareCommandGroup_ShowsItsHelp_AndSucceeds()
    {
        var captured = Report("bpa");

        Assert.Equal(0, captured.ExitCode);
        Assert.Matches(@"(?m)^USAGE\r?\n  tx bpa <command> \[options\]", captured.Stdout);
        Assert.Equal("", captured.Stderr);
    }

    [Fact]
    public void UnknownOption_OnANestedCommand_PointsAtItsFullHelpPath()
    {
        var captured = Report("bpa", "run", "--bogus", "--rule");

        Assert.Contains("tx bpa run --help", captured.Stderr);
    }

    private static ConsoleCapture.Captured Report(params string[] args)
    {
        var parsed = TestRoot.Full().Parse(args);
        Assert.NotEmpty(parsed.Errors);
        var captured = ConsoleCapture.Run(() => UsageErrors.Report(parsed, args), captureAnsiConsole: true);
        return captured with { Stdout = Plain(captured.Stdout), Stderr = Plain(captured.Stderr) };
    }

    private static Command Command(string name)
        => TestRoot.Full().Subcommands.Single(c => c.Name == name);

    private static string Render(Command command, int width)
        => Plain(ConsoleCapture.Run(() => SpectreHelpAction.Write(command, concise: false, width), captureAnsiConsole: true)
            .Stdout);

    private static string Plain(string text) => Ansi().Replace(text, "").Replace("\r\n", "\n");

    [GeneratedRegex(@"\x1b\[[0-9;]*m")]
    private static partial Regex Ansi();

    [GeneratedRegex(@"(?<!e\.g)(?<!i\.e)\. [A-Z'""(]")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(@"\bDefaults? (to|is)\b|Default:")]
    private static partial Regex DefaultsSentence();
}
