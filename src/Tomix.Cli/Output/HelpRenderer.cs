using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using Spectre.Console;

namespace Tomix.Cli.Output;

internal sealed class SpectreHelpAction : SynchronousCommandLineAction
{
    /// <summary>
    /// The built-in <see cref="HelpAction"/> clears parse errors so that <c>cmd --help</c>
    /// succeeds even when required arguments are missing. This replacement must do the same,
    /// or every command with a required positional exits 2 on <c>--help</c>.
    /// </summary>
    public override bool ClearsParseErrors => true;

    internal const string DocsUrl = "https://bgarcevic.github.io/tomix-cli/";

    /// <summary>Help never wraps wider than this, however wide the terminal (clap's default too).</summary>
    internal const int MaxWidth = 100;

    /// <summary>A label column wider than this moves descriptions onto their own lines.</summary>
    private const int MaxLabelColumn = 40;

    /// <summary>Indent of a description placed under its label (the stacked layout).</summary>
    private const int StackedIndent = 8;

    internal static readonly (string Heading, string[] Commands)[] RootSections =
    [
        ("Discover", ["summary", "ls", "get", "find", "deps", "query"]),
        ("Modify", ["add", "set", "mv", "rm", "replace", "format", "interactive", "serve", "ui"]),
        ("Connect", ["connect", "deploy", "refresh", "save", "auth"]),
        ("Validate", ["bpa", "validate", "test", "vertipaq", "diff", "doctor"]),
        ("Manage", ["config", "profile", "init", "completion", "skills", "stage", "update"]),
    ];

    /// <summary>
    /// Detail that is too long for a command's one-line description (which also appears in the
    /// root command list): printed on the command's own help page, under the description.
    /// </summary>
    internal static readonly Dictionary<string, string> CommandNotes = new(StringComparer.Ordinal)
    {
        ["bpa rules add"] = "Edits your config-dir bpa-rules.json, which bpa run loads, unless --rules-file names another file.",
        ["bpa rules ignore"] = "Writes the model's ignore annotation (shared with everyone who uses the model). --user ignores the rule only for you, on this machine, for every model.",
        ["bpa rules init"] = "Creates your config-dir bpa-rules.json, or the file --rules-file names.",
        ["bpa rules unignore"] = "Removes the rule from the model's ignore annotation. --user undoes 'ignore --user'; a rule runs only when neither level ignores it.",
        ["connect"] = "With no arguments, shows the active connection. --recent reconnects to a recently used model. --clear --all forgets the connection in every session.",
        ["deps"] = "A shortcut for tx get --deps (or --unused); both run the same read pipeline.",
        ["diff"] = "Exit codes: 0 = identical, 1 = differences found, 2 = error.",
        ["get"] = "One object shows its properties; a wildcard or container path lists every match. --ls, --where, --deps and --unused select and analyze. get returns objects; tx find searches property text and returns match sites (name filtering deliberately overlaps).",
        ["format"] = "Formats an inline expression (--expression), one object (--path), or every expression in the model.",
        ["interactive"] = "Alias: shell. Edits stay in memory until 'save'. Inside the session, 'connect' switches models and 'undo', 'redo', 'begin', 'commit', 'rollback', 'status', 'history' and 'exit' also work. Piped input runs as a script that stops at the first failure.",
        ["ls"] = "A shortcut for tx get --ls; both run the same read pipeline.",
        ["query"] = "The query comes from the positional argument, --query, --file, or stdin.",
        ["refresh"] = "Runs an automatic refresh unless --refresh-type says otherwise.",
        ["serve"] = "Speaks the tomix session protocol (JSON-RPC 2.0, Content-Length framing) on stdin and stdout; see docs/protocol.md. Without a model, the client opens one with session.open. The log goes to stderr unless --log names a file. When tx ui holds the model, it joins that session instead of opening another.",
        ["ui"] = "Prints the page's URL, with the session token, on stdout; agents join the same session through tx serve. Stops on Ctrl+C (asking again when changes are unsaved), or --grace seconds after the last client leaves, unless changes are unsaved.",
        ["test"] = "--update records snapshots; --trx and --ci produce pipeline output.",
        ["skills"] = "Writes the skill to .claude/skills/tomix (Claude Code) and .agents/skills/tomix (Codex and other Agent Skills harnesses). Copies you edited are left alone unless you pass --force.",
        ["validate"] = "--ci prints CI log groups; --trx writes a test-results file.",
    };

    internal static readonly Dictionary<string, string[]> CommandExamples = new(StringComparer.Ordinal)
    {
        ["ls"] = [
            "tx ls",
            "tx ls --type table",
            "tx ls Sa*",
            "tx ls \"'Net Sales'/Measures\"",
            "tx ls --paths-only --type measure",
        ],
        ["summary"] = [
            "tx summary",
            "tx summary ./model.tmdl",
            "tx summary --output-format json",
        ],
        ["get"] = [
            "tx get \"Table[Measure]\"",
            "tx get Revenue -t measure",
            "tx get Sales --all",
            "tx get Sales/Measures/Revenue --output-format json",
            "tx get \"Sa*\"",
            "tx get Measures --where \"Name=*margin*\"",
            "tx get Columns --where DataType=String --where IsHidden=true",
            "tx get Sales/Revenue --deps downstream --deep",
            "tx get --unused --hidden",
        ],
        ["find"] = [
            "tx find CALCULATE",
            "tx find \"SUM(Sales\" --in expressions",
        ],
        ["deps"] = [
            "tx deps \"Table[Measure]\"",
            "tx deps Sales --downstream",
        ],
        ["query"] = [
            "tx query \"EVALUATE Sales\"",
            "tx query --query \"EVALUATE Sales\"",
            "tx query --file query.dax --limit 10",
            "tx query --query \"EVALUATE VALUES(Sales[Region])\" --output-format json",
        ],
        ["test"] = [
            "tx test ./tests -s MyWorkspace -d MyModel",
            "tx test ./tests --update",
            "tx test ./tests --filter \"totals/*\" --trx results.trx --ci vsts",
        ],
        ["add"] = [
            "tx add Sales/Revenue -t Measure -e \"SUM(Sales[Amount])\"",
            "tx add Sales/Revenue -t Measure -e - < revenue.dax",
            "tx add Sales/Margin -t Measure -e \"[Profit] / [Sales]\" --set formatString=0.0%",
        ],
        ["set"] = [
            "tx set \"Table[Measure]\" --set expression=\"CALCULATE(SUM(Sales[Amount]))\"",
            "tx set \"Sales[Total Sales]\" --set displayFolder=KPIs --save",
            "tx set Sales --set name=Sales_v2",
        ],
        ["mv"] = [
            "tx mv \"Sales/Old Name\" \"Sales/New Name\" --save",
            "tx mv Sales SalesData",
        ],
        ["rm"] = [
            "tx rm Sales/Obsolete",
            "tx rm Staging --save",
        ],
        ["replace"] = [
            "tx replace \"[OrderDate]\" \"[ShipDate]\"",
            "tx replace \"old_name\" \"new_name\" --in expressions",
        ],
        ["format"] = [
            "tx format",
            "tx format -e \"CALCULATE(sum(sales[amt]))\"",
            "tx format --path \"Table[Measure]\"",
        ],
        ["connect"] = [
            "tx connect",
            "tx connect --remote",
            "tx connect MyWorkspace Sales",
            "tx connect ./model.tmdl",
            "tx connect ./model.tmdl -w",
            "tx connect --local",
            "tx connect ./model.tmdl -w MyWorkspace Sales",
            "tx connect --clear --all",
        ],
        ["deploy"] = [
            "tx deploy ./model.tmdl",
            "tx deploy ./model.tmdl --yes",
            "tx deploy ./model.tmdl --profile prod",
            "tx deploy ./model.bim --skip-bpa",
            "tx deploy ./model.tmdl --bpa-fail-on warning",
        ],
        ["refresh"] = [
            "tx refresh",
            "tx refresh --refresh-type full",
            "tx refresh --table Sales --table Customers",
            "tx refresh --partition Sales.FY2024",
            "tx refresh --refresh-type clearvalues --yes",
        ],
        ["save"] = [
            "tx save ./model.tmdl --serialization bim",
            "tx save --output-format json",
        ],
        ["auth"] = [
            "tx auth login",
            "tx auth login --auth spn --client-id $SPN_ID",
            "tx auth status",
            "tx auth logout",
        ],
        ["bpa"] = [
            "tx bpa run",
            "tx bpa run --errors",
            "tx bpa run --output-format json",
            "tx bpa run --fix",
        ],
        ["bpa rules add"] = [
            "tx bpa rules add --id HIDE_KEYS --name Keys --scope Column --expression IsKey",
            "tx bpa rules add --id NO_KPIS --name \"No KPIs\" --scope KPI --expression true",
        ],
        ["bpa rules set"] = [
            "tx bpa rules set NO_DOUBLE --severity error",
            "tx bpa rules --rules-file team.json set NO_DOUBLE --category Performance",
        ],
        ["bpa rules remove"] = [
            "tx bpa rules remove NO_DOUBLE",
        ],
        ["bpa rules ignore"] = [
            "tx bpa rules ignore HIDE_FOREIGN_KEYS --save",
            "tx bpa rules ignore HIDE_FOREIGN_KEYS --user",
        ],
        ["bpa rules unignore"] = [
            "tx bpa rules unignore HIDE_FOREIGN_KEYS --save",
            "tx bpa rules unignore HIDE_FOREIGN_KEYS --user",
        ],
        ["bpa rules init"] = [
            "tx bpa rules init",
            "tx bpa rules --rules-file team.json init",
        ],
        ["bpa run"] = [
            "tx bpa run",
            "tx bpa run --errors --details",
            "tx bpa run --fix",
            "tx bpa run --fix --save",
            "tx bpa run --ci github --fail-on warning",
        ],
        ["validate"] = [
            "tx validate",
            "tx validate --ci github",
            "tx validate --trx results.trx",
        ],
        ["vertipaq"] = [
            "tx vertipaq",
            "tx vertipaq Sales --detail",
            "tx vertipaq --stats --all --top 10",
            "tx vertipaq --export stats.vpax",
            "tx vertipaq --import stats.vpax --relationships",
        ],
        ["diff"] = [
            "tx diff ./v1.tmdl ./v2.tmdl",
            "tx diff ./v1.bim ./v2.bim --output-format json",
        ],
        ["doctor"] = [
            "tx doctor",
        ],
        ["config"] = [
            "tx config show",
            "tx config set noColor true",
        ],
        ["profile"] = [
            "tx profile list",
            "tx profile set dev -s MyWorkspace -d Sales",
            "tx connect --profile dev",
        ],
        ["profile set"] = [
            "tx profile set dev -s MyWorkspace -d Sales",
            "tx profile set dev --from-active",
        ],
        ["skills"] = [
            "tx skills install",
            "tx skills install --agent codex --user",
            "tx skills status",
        ],
        ["interactive"] = [
            "tx interactive ./model",
            "tx shell",
            "tx interactive ./model --echo --discard-on-exit",
        ],
        ["serve"] = [
            "tx serve ./model",
            "tx serve --log serve.log",
        ],
        ["ui"] = [
            "tx ui ./model --open",
            "tx ui ./model --port 7411 --grace 300",
        ],
        ["init"] = [
            "tx init",
            "tx init ./my-model",
        ],
        ["completion"] = [
            "tx completion powershell | Invoke-Expression",
            "tx completion bash >> ~/.bashrc",
        ],
        ["stage"] = [
            "tx stage",
            "tx stage commit",
            "tx stage discard",
        ],
        ["update"] = [
            "tx update --check",
            "tx update",
            "tx update --version 0.2.0 --yes",
        ],
    };

    public override int Invoke(ParseResult parseResult)
    {
        Write(parseResult.CommandResult.Command, concise: false, TerminalWidth());
        return 0;
    }

    /// <summary>
    /// The width help wraps to: the terminal's, capped at <see cref="MaxWidth"/>. Redirected
    /// output is not wrapped, so a pager or file gets whole lines and never a hard break.
    /// </summary>
    internal static int TerminalWidth()
        => Console.IsOutputRedirected ? int.MaxValue : Math.Clamp(AnsiConsole.Profile.Width, 40, MaxWidth);

    /// <summary>
    /// Writes help for <paramref name="command"/>. <paramref name="concise"/> is the bare
    /// <c>tx</c> form: the command list without the global options.
    /// </summary>
    internal static void Write(Command command, bool concise, int width)
    {
        // Lines are wrapped here, with hanging indents; Spectre must not wrap them again.
        var originalWidth = AnsiConsole.Profile.Width;
        AnsiConsole.Profile.Width = int.MaxValue;

        try
        {
            var help = new HelpWriter(Math.Max(width, 40));
            if (command is RootCommand)
                help.WriteRoot(command, concise);
            else
                help.WriteCommand(command);
        }
        finally
        {
            AnsiConsole.Profile.Width = originalWidth;
        }
    }

    /// <summary>The page path of a command: "bpa run" for <c>tx bpa run</c>, "" for the root.</summary>
    internal static string CommandPath(Command command)
    {
        var chain = new List<string>();
        for (var current = command; current is not null and not RootCommand;
             current = current.Parents.OfType<Command>().FirstOrDefault())
        {
            chain.Insert(0, current.Name);
        }

        return string.Join(' ', chain);
    }

    /// <summary>
    /// The option's names as shown in help: short aliases first, then the long name. Long aliases
    /// (<c>--recents</c>, <c>--compat</c>) and the help option's legacy spellings (<c>-?</c>,
    /// <c>/?</c>, <c>/h</c>) still parse but are not listed; the reference docs cover them.
    /// </summary>
    internal static IReadOnlyList<string> DisplayNames(Option option)
    {
        var shorts = option.Aliases.Prepend(option.Name)
            .Where(n => n.Length == 2 && n[0] == '-' && n != "-?")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal);
        return option.Name.StartsWith("--", StringComparison.Ordinal)
            ? shorts.Append(option.Name).ToList()
            : shorts.ToList();
    }

    /// <summary>The value placeholder: <c>&lt;path&gt;</c>, or <c>[&lt;n&gt;]</c> when the value is optional.</summary>
    internal static string? Placeholder(Option option)
    {
        if (!HelpPlaceholders.TakesValue(option))
            return null;

        var name = $"<{option.HelpName ?? option.Name.TrimStart('-')}>";
        // Only a single optional value is bracketed; repeatable options take one value per use.
        return option.Arity is { MinimumNumberOfValues: 0, MaximumNumberOfValues: 1 } ? $"[{name}]" : name;
    }

    /// <summary>The save flags in the order a reader weighs them, whatever order a command declares them in.</summary>
    private static readonly string[] SaveOrder =
        ["--save", "--save-to", "--stage", "--revert", "--serialization", "--overwrite", "--force", "--no-sync"];

    private readonly record struct Row(string Label, string StyledLabel, string Description);

    private sealed class HelpWriter(int width)
    {
        public void WriteRoot(Command root, bool concise)
        {
            AnsiConsole.MarkupLine($"{Styling.Bold("tx")} {Styling.Muted("—")} {Styling.MarkupEscape(root.Description ?? "")}");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"{Styling.Title("Usage:")} {Styling.Bold("tx")} {Styling.Value("<command>")} {Styling.Option("[options]")}");
            AnsiConsole.WriteLine();

            WriteSectionedCommands(root);

            if (!concise)
            {
                AnsiConsole.MarkupLine(Styling.Title("Global options:"));
                // Derived from the actual root options (help/version plus the recursive globals)
                // so the root help can never drift from what the parser accepts.
                WriteOptionRows(root.Options.Where(option => !option.Hidden).ToList());
                AnsiConsole.WriteLine();
                WriteFooter("Run 'tx help <command>' for a command's options and examples.");
            }
            else
            {
                WriteFooter("Run 'tx help <command>' for a command's options, or 'tx --help' for global options.");
            }
        }

        public void WriteCommand(Command command)
        {
            var path = CommandPath(command);

            if (!string.IsNullOrEmpty(command.Description))
            {
                WriteParagraph(command.Description, 0);
                AnsiConsole.WriteLine();
            }

            if (CommandNotes.TryGetValue(path, out var notes))
            {
                WriteParagraph(notes, 0, Styling.Muted);
                AnsiConsole.WriteLine();
            }

            WriteUsage(command, path);

            if (command.Subcommands.Any(sc => !sc.Hidden))
            {
                AnsiConsole.MarkupLine(Styling.Title("Commands:"));
                WriteCommandRows(command.Subcommands.Where(sc => !sc.Hidden).ToList(), includeArguments: true);
                AnsiConsole.WriteLine();
            }

            var arguments = command.Arguments.Where(a => !a.Hidden).ToList();
            if (arguments.Count > 0)
            {
                AnsiConsole.MarkupLine(Styling.Title("Arguments:"));
                WriteRows(arguments.Select(a =>
                {
                    var label = ArgumentLabel(a);
                    return new Row(label, Styling.Value(label), a.Description ?? "");
                }).ToList());
                AnsiConsole.WriteLine();
            }

            var (local, global) = GroupedOptions(command);

            // Untagged options first, then the command's own groups in first-seen order, then the
            // shared save flags, then the compatibility forms.
            foreach (var group in local.GroupBy(o => HelpGroups.Of(o) ?? "")
                         .OrderBy(g => g.Key switch { "" => 0, HelpGroups.Save => 2, _ => 1 }))
            {
                AnsiConsole.MarkupLine(Styling.Title($"{(group.Key.Length == 0 ? "Options" : group.Key)}:"));
                WriteOptionRows(group.Key == HelpGroups.Save
                    ? group.OrderBy(o => Array.IndexOf(SaveOrder, o.Name) is var i and >= 0 ? i : SaveOrder.Length).ToList()
                    : group.ToList());
                AnsiConsole.WriteLine();
            }

            if (CommandExamples.TryGetValue(path, out var examples))
            {
                AnsiConsole.MarkupLine(Styling.Title("Examples:"));
                foreach (var example in examples)
                    AnsiConsole.MarkupLine($"  {Styling.Path(example)}");
                AnsiConsole.WriteLine();
            }

            if (global.Count > 0)
            {
                // Each page used to list all thirteen globals in full: nearly half of every
                // command's help. They are the same everywhere, so name them and point at the root.
                var names = string.Join(", ", global.Select(o => o.Name));
                var heading = "Global options:";
                var lines = Wrap($"{names} (see 'tx --help')", width - heading.Length - 1);
                AnsiConsole.MarkupLine($"{Styling.Title(heading)} {Styling.Muted(lines[0])}");
                foreach (var line in lines.Skip(1))
                    AnsiConsole.MarkupLine($"{new string(' ', heading.Length + 1)}{Styling.Muted(line)}");
            }
        }

        private void WriteUsage(Command command, string path)
        {
            var usage = $"{Styling.Title("Usage:")} {Styling.Bold($"tx {path}")}";
            foreach (var arg in command.Arguments.Where(a => !a.Hidden))
                usage += " " + Styling.Value(ArgumentLabel(arg, bracketOptional: true));
            if (command.Subcommands.Any(sc => !sc.Hidden))
                usage += " " + Styling.Value("<command>");
            usage += " " + Styling.Option("[options]");
            AnsiConsole.MarkupLine(usage);
            AnsiConsole.WriteLine();
        }

        private void WriteSectionedCommands(Command root)
        {
            var subcommands = root.Subcommands.Where(sc => !sc.Hidden).ToDictionary(sc => sc.Name, StringComparer.Ordinal);
            var sections = RootSections
                .Select(s => (s.Heading, Commands: s.Commands.Where(subcommands.ContainsKey).Select(n => subcommands[n]).ToList()))
                .ToList();

            // Safety net: a registered command missing from RootSections must still show up in
            // help rather than silently vanishing.
            var listed = RootSections.SelectMany(section => section.Commands).ToHashSet(StringComparer.Ordinal);
            var unlisted = subcommands.Values.Where(sc => !listed.Contains(sc.Name)).ToList();
            if (unlisted.Count > 0)
                sections.Add(("Other", unlisted));

            // One label column across every section, so the descriptions line up down the page.
            var labelWidth = sections.SelectMany(s => s.Commands).Max(c => CommandLabel(c, false).Length);
            foreach (var (heading, commands) in sections)
            {
                AnsiConsole.MarkupLine(Styling.Title($"{heading}:"));
                WriteCommandRows(commands, includeArguments: false, labelWidth);
                AnsiConsole.WriteLine();
            }
        }

        private void WriteCommandRows(List<Command> commands, bool includeArguments, int? labelWidth = null)
        {
            var rows = commands.Select(c =>
            {
                var label = CommandLabel(c, includeArguments);
                var styled = Styling.Bold(c.Name) + label[c.Name.Length..] switch
                {
                    "" => "",
                    var args => " " + Styling.Value(args.TrimStart()),
                };
                return new Row(label, styled, c.Description ?? "");
            }).ToList();
            WriteRows(rows, labelWidth);
        }

        private void WriteOptionRows(List<Option> options)
        {
            // Long names line up in their own column when any option in the block has a short alias.
            var anyShort = options.Any(o => DisplayNames(o)[0] is { } first && !first.StartsWith("--", StringComparison.Ordinal));
            var rows = options.Select(option =>
            {
                var names = DisplayNames(option);
                var joined = string.Join(", ", names);
                if (anyShort && names[0].StartsWith("--", StringComparison.Ordinal))
                    joined = "    " + joined;

                var placeholder = Placeholder(option);
                var label = placeholder is null ? joined : $"{joined} {placeholder}";
                var styled = Styling.Option(joined) + (placeholder is null ? "" : " " + Styling.Value(placeholder));
                return new Row(label, styled, option.Description ?? "");
            }).ToList();
            WriteRows(rows);
        }

        private void WriteRows(List<Row> rows, int? labelWidth = null)
        {
            if (rows.Count == 0)
                return;

            const int indent = 2;
            var widest = labelWidth ?? rows.Max(r => r.Label.Length);
            var column = indent + widest + 2;
            var stacked = column > MaxLabelColumn;

            foreach (var row in rows)
            {
                if (stacked)
                {
                    AnsiConsole.MarkupLine($"  {row.StyledLabel}");
                    if (row.Description.Length > 0)
                        WriteParagraph(row.Description, StackedIndent);
                    continue;
                }

                var lines = Wrap(row.Description, width - column);
                var padding = new string(' ', column - indent - row.Label.Length);
                AnsiConsole.MarkupLine($"  {row.StyledLabel}{padding}{Styling.MarkupEscape(lines[0])}");
                foreach (var line in lines.Skip(1))
                    AnsiConsole.MarkupLine($"{new string(' ', column)}{Styling.MarkupEscape(line)}");
            }
        }

        private void WriteParagraph(string text, int indent, Func<string, string>? style = null)
        {
            style ??= Styling.MarkupEscape;
            foreach (var line in Wrap(text, width - indent))
                AnsiConsole.MarkupLine($"{new string(' ', indent)}{style(line)}");
        }

        private void WriteFooter(string hint)
        {
            WriteParagraph(hint, 0, Styling.Muted);
            AnsiConsole.MarkupLine(Styling.Muted($"Docs: {DocsUrl}"));
        }
    }

    /// <summary>
    /// The name, plus its positional arguments on a group's own page. The root list shows names
    /// only: arguments widened its label column enough to wrap descriptions at 80 columns, and
    /// every command's page states them on its Usage line.
    /// </summary>
    private static string CommandLabel(Command command, bool includeArguments)
        => includeArguments
            ? string.Join(' ', command.Arguments.Where(a => !a.Hidden)
                .Select(a => ArgumentLabel(a, bracketOptional: true)).Prepend(command.Name))
            : command.Name;

    private static string ArgumentLabel(Argument argument, bool bracketOptional = false)
        => bracketOptional && argument.Arity.MinimumNumberOfValues == 0 ? $"[{argument.Name}]" : $"<{argument.Name}>";

    private static (List<Option> Local, List<Option> Global) GroupedOptions(Command command)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var local = new List<Option>();
        var global = new List<Option>();

        // Every command carries its own --help; it is listed with the globals instead.
        foreach (var opt in command.Options.Where(o => !o.Hidden && o is not HelpOption))
        {
            if (seen.Add(opt.Name))
                local.Add(opt);
        }

        for (var parent = command.Parents.OfType<Command>().FirstOrDefault(); parent is not null;
             parent = parent.Parents.OfType<Command>().FirstOrDefault())
        {
            foreach (var opt in parent.Options.Where(o => !o.Hidden && o.Recursive))
            {
                if (seen.Add(opt.Name))
                    global.Add(opt);
            }
        }

        if (command.Options.OfType<HelpOption>().FirstOrDefault() is { } help && seen.Add(help.Name))
            global.Add(help);

        return (local, global);
    }

    /// <summary>
    /// Word-wraps <paramref name="text"/> to <paramref name="width"/> columns. Always returns at
    /// least one line; a word longer than the width is broken rather than overflowing.
    /// </summary>
    internal static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        if (width <= 0 || text.Length <= width)
        {
            lines.Add(text);
            return lines;
        }

        var current = new System.Text.StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var remaining = word;
            while (remaining.Length > 0)
            {
                var space = current.Length == 0 ? 0 : 1;
                if (current.Length + space + remaining.Length <= width)
                {
                    if (space == 1)
                        current.Append(' ');
                    current.Append(remaining);
                    remaining = "";
                }
                else if (current.Length > 0)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    lines.Add(remaining[..width]);
                    remaining = remaining[width..];
                }
            }
        }

        if (current.Length > 0 || lines.Count == 0)
            lines.Add(current.ToString());
        return lines;
    }
}
