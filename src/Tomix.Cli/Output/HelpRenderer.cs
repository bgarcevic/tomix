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

    private static readonly (string Heading, string[] Commands)[] RootSections =
    [
        ("Discover", ["ls", "get", "find", "deps"]),
        ("Modify", ["add", "set", "mv", "rm", "replace", "format", "script", "macro"]),
        ("Connect", ["connect", "deploy", "refresh", "load", "save", "auth", "session"]),
        ("Validate", ["bpa", "validate", "diff", "doctor"]),
        ("Manage", ["config", "profile", "init", "completion", "stage", "interactive"]),
    ];

    private static readonly string[] NotImplementedCommands =
        ["incremental-refresh", "query", "test", "vertipaq"];

    private static readonly Dictionary<string, string[]> CommandExamples = new(StringComparer.Ordinal)
    {
        ["ls"] = [
            "tx ls",
            "tx ls --type table",
            "tx ls Sa*",
            "tx ls --paths-only --type measure",
        ],
        ["get"] = [
            "tx get \"Table[Measure]\"",
            "tx get Revenue -t measure",
            "tx get Sales/Measures/Revenue --output-format json",
        ],
        ["find"] = [
            "tx find CALCULATE",
            "tx find \"SUM(Sales\" --in expressions",
        ],
        ["deps"] = [
            "tx deps \"Table[Measure]\"",
            "tx deps tables/Sales --downstream",
        ],
        ["add"] = [
            "tx add tables/Sales/measures/Revenue -i \"CALCULATE(SUM(Sales[Amount]))\"",
            "tx add tables/Sales/measures/Revenue -i - < query.txt",
            "tx add Sales/Revenue -t Measure -i \"CALCULATE(SUM(Sales[Amount]))\"",
        ],
        ["set"] = [
            "tx set \"Table[Measure]\" -q \"CALCULATE(SUM(Sales[Amount]))\"",
            "tx set tables/Sales/Name -i \"Sales_v2\"",
        ],
        ["mv"] = [
            "tx mv tables/Sales/measures/OldName tables/Sales/measures/NewName",
            "tx mv tables/Sales tables/SalesData",
        ],
        ["rm"] = [
            "tx rm tables/Sales/measures/ObsoleteMeasure",
            "tx rm tables/Staging --save",
        ],
        ["replace"] = [
            "tx replace \"[OrderDate]\" \"[ShipDate]\"",
            "tx replace \"old_name\" \"new_name\" --type measure",
        ],
        ["format"] = [
            "tx format",
            "tx format -e \"CALCULATE(sum(sales[amt]))\"",
            "tx format -p \"Table[Measure]\"",
        ],
        ["script"] = [
            "tx script -e \"Model.Tables.Count\"",
            "tx script transform.csx --save",
            "tx script -e \"Model.Tables[\\\"Sales\\\"].Name\" --output-format json",
        ],
        ["connect"] = [
            "tx connect",
            "tx connect MyWorkspace Sales",
            "tx connect ./model.tmdl",
            "tx connect --local",
            "tx connect ./model.tmdl -w MyWorkspace Sales",
        ],
        ["deploy"] = [
            "tx deploy ./model.tmdl",
            "tx deploy ./model.tmdl --dry-run",
            "tx deploy ./model.bim --skip-bpa",
        ],
        ["load"] = [
            "tx load ./model.tmdl",
            "tx load --output-format json",
        ],
        ["save"] = [
            "tx save ./model.tmdl --serialization bim",
            "tx save --output-format tmdl",
        ],
        ["auth"] = [
            "tx auth login",
            "tx auth login --auth spn --client-id $SPN_ID",
            "tx auth status",
            "tx auth logout",
        ],
        ["session"] = [
            "tx session",
            "tx session --reset",
        ],
        ["bpa"] = [
            "tx bpa",
            "tx bpa --format json",
            "tx bpa --severity error",
        ],
        ["validate"] = [
            "tx validate",
            "tx validate --ci",
            "tx validate --trx",
        ],
        ["diff"] = [
            "tx diff ./v1.tmdl ./v2.tmdl",
            "tx diff ./v1.bim ./v2.bim --output-format json",
        ],
        ["doctor"] = [
            "tx doctor",
        ],
        ["config"] = [
            "tx config list",
            "tx config set noColor true",
        ],
        ["profile"] = [
            "tx profile list",
            "tx profile add dev -s MyWorkspace -d Sales",
            "tx profile activate dev",
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
            "tx stage revert",
        ],
        ["interactive"] = [
            "tx interactive",
            "tx interactive ./model.tmdl",
        ],
    };

    public override int Invoke(ParseResult parseResult)
    {
        var command = parseResult.CommandResult.Command;
        var originalWidth = AnsiConsole.Profile.Width;
        AnsiConsole.Profile.Width = int.MaxValue;

        try
        {
            if (command is RootCommand)
                WriteRoot(command);
            else
                WriteCommand(command);
        }
        finally
        {
            AnsiConsole.Profile.Width = originalWidth;
        }

        return 0;
    }

    private static void WriteRoot(Command root)
    {
        AnsiConsole.MarkupLine($"{Styling.Title("tomix")} {Styling.Muted("- open source semantic model CLI")}");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(Styling.Title("Usage:"));
        AnsiConsole.MarkupLine($"  {Styling.Bold("tx")} {Styling.Bold("[command]")} {Styling.Option("[options]")}");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(Styling.Title("Global options:"));

        var globalOptions = new (string Flags, string Description)[]
        {
            ("-h, /h, -?, /?, --help", "Show help and usage information"),
            ("--version", "Show version information"),
            ("-m, --model <model>", "Path to semantic model (TMDL folder, .bim file, or TE folder)"),
            ("--output-format <output-format>", "Stdout format: text (default), json, csv, tmsl (alias: bim), tmdl. Not all formats are supported by every command."),
            ("--error-format <error-format>", "Stderr format for errors/warnings/hints: text (default) or json. Other values fall back to text."),
            ("-s, --server <server>", "Workspace name or endpoint (e.g., MyWorkspace, powerbi://..., asazure://..., localhost)"),
            ("-d, --database <database>", "Semantic model name on the workspace"),
            ("--local", "Connect to a locally running Power BI Desktop instance (Windows only)"),
            ("--auth <auth>", "Auth method: auto, interactive, spn, env, managed-identity (default: auto)"),
            ("--recent <recent>", "Use a recently used model. No value = interactive picker, N = Nth most recent (1 = last used)."),
            ("--debug", "Enable debug logging to stderr (connection strings, auth flow, timing)"),
            ("--non-interactive", "Disable all interactive prompts. Fail with an actionable error if required input is missing.")
        };

        var globalFlagsWidth = globalOptions.Max(o => o.Flags.Length) + 2;
        foreach (var (flags, desc) in globalOptions)
        {
            var styled = StyleOptionLabel(flags);
            var padding = new string(' ', globalFlagsWidth - flags.Length);
            AnsiConsole.MarkupLine($"  {styled}{padding}{Styling.MarkupEscape(desc)}");
        }

        AnsiConsole.WriteLine();
        WriteSectionedCommands(root);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(Styling.Muted("Use `tx <command> --help` for command-specific options."));
    }

    private static void WriteSectionedCommands(Command root)
    {
        var subcommandMap = root.Subcommands.ToDictionary(sc => sc.Name, StringComparer.Ordinal);

        foreach (var (heading, commandNames) in RootSections)
        {
            AnsiConsole.MarkupLine(Styling.Title($"{heading}:"));
            var sectionCommands = commandNames.Where(subcommandMap.ContainsKey).Select(name => subcommandMap[name]).ToList();
            WriteCommandRows(sectionCommands);
            AnsiConsole.WriteLine();
        }

        var notImplemented = NotImplementedCommands.Where(subcommandMap.ContainsKey).ToList();
        if (notImplemented.Count > 0)
        {
            AnsiConsole.MarkupLine(Styling.Title("Not yet implemented:"));
            WriteCommandRows(notImplemented.Select(name => subcommandMap[name]).ToList());
            AnsiConsole.WriteLine();
        }
    }

    private static void WriteCommandRows(List<Command> commands)
    {
        var rows = commands.Select(sc =>
        {
            var plainLabel = sc.Name;
            var styledParts = new List<string> { Styling.Bold(sc.Name) };
            foreach (var arg in sc.Arguments.Where(a => !a.Hidden))
            {
                if (arg.Arity.MinimumNumberOfValues == 0)
                {
                    plainLabel += $" [{arg.Name}]";
                    styledParts.Add(Styling.Value($"[{arg.Name}]"));
                }
                else
                {
                    plainLabel += $" <{arg.Name}>";
                    styledParts.Add(Styling.Value($"<{arg.Name}>"));
                }
            }
            return (PlainLabel: plainLabel, StyledLabel: string.Join(" ", styledParts), Description: sc.Description ?? "");
        }).ToList();

        if (rows.Count == 0)
            return;

        var labelWidth = rows.Max(r => r.PlainLabel.Length) + 2;
        foreach (var row in rows)
        {
            var padding = new string(' ', labelWidth - row.PlainLabel.Length);
            AnsiConsole.MarkupLine($"  {row.StyledLabel}{padding}{Styling.MarkupEscape(row.Description)}");
        }
    }

    private static void WriteCommand(Command command)
    {
        var parentChain = GetParentChain(command);
        var hasSubcommands = command.Subcommands.Count > 0;

        if (!string.IsNullOrEmpty(command.Description))
        {
            AnsiConsole.MarkupLine(Styling.Title("Description:"));
            AnsiConsole.MarkupLine($"  {Styling.MarkupEscape(command.Description)}");
            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine(Styling.Title("Usage:"));
        var usageParts = new List<string> { "tx" };
        usageParts.AddRange(parentChain);
        var usageLine = Styling.Bold(string.Join(" ", usageParts));

        foreach (var arg in command.Arguments.Where(a => !a.Hidden))
        {
            if (arg.Arity.MinimumNumberOfValues == 0)
                usageLine += " " + Styling.Value($"[{arg.Name}]");
            else
                usageLine += " " + Styling.Value($"<{arg.Name}>");
        }

        if (hasSubcommands)
            usageLine += " " + Styling.Bold("[command]");

        usageLine += " " + Styling.Option("[options]");
        AnsiConsole.MarkupLine($"  {usageLine}");
        AnsiConsole.WriteLine();

        if (hasSubcommands)
        {
            WriteCommandsSection(command);
            AnsiConsole.WriteLine();
        }

        var arguments = command.Arguments.Where(a => !a.Hidden).ToList();
        if (arguments.Count > 0)
        {
            AnsiConsole.MarkupLine(Styling.Title("Arguments:"));
            WriteArgumentRows(arguments.Select(a => ($"<{a.Name}>", a.Description ?? "")));
            AnsiConsole.WriteLine();
        }

        var (localOptions, globalOptions) = GetGroupedOptions(command);

        if (localOptions.Count > 0)
        {
            AnsiConsole.MarkupLine(Styling.Title("Options:"));
            WriteOptionRows(localOptions.Select(o => (FormatOptionAliases(o), o.Description ?? "")));
            AnsiConsole.WriteLine();
        }

        if (globalOptions.Count > 0)
        {
            AnsiConsole.MarkupLine(Styling.Title("Global options:"));
            WriteOptionRows(globalOptions.Select(o => (FormatOptionAliases(o), o.Description ?? "")));
            AnsiConsole.WriteLine();
        }

        var commandKey = string.Join(" ", parentChain);
        if (CommandExamples.TryGetValue(commandKey, out var examples))
        {
            AnsiConsole.MarkupLine(Styling.Title("Examples:"));
            foreach (var example in examples)
                AnsiConsole.MarkupLine($"  {Styling.Path(example)}");
            AnsiConsole.WriteLine();
        }
    }

    private static void WriteCommandsSection(Command command)
    {
        AnsiConsole.MarkupLine(Styling.Title("Commands:"));
        WriteCommandRows(command.Subcommands.ToList());
    }

    private static void WriteOptionRows(IEnumerable<(string Label, string Description)> rows)
    {
        var rowList = rows.ToList();
        var labelWidth = rowList.Max(r => r.Label.Length) + 2;
        foreach (var (label, description) in rowList)
        {
            var styled = StyleOptionLabel(label);
            var padding = new string(' ', labelWidth - label.Length);
            AnsiConsole.MarkupLine($"  {styled}{padding}{Styling.MarkupEscape(description)}");
        }
    }

    private static void WriteArgumentRows(IEnumerable<(string Label, string Description)> rows)
    {
        var rowList = rows.ToList();
        var labelWidth = rowList.Max(r => r.Label.Length) + 2;
        foreach (var (label, description) in rowList)
            AnsiConsole.MarkupLine($"  {Styling.Value(label.PadRight(labelWidth))}{Styling.MarkupEscape(description)}");
    }

    private static (List<Option> Local, List<Option> Global) GetGroupedOptions(Command command)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var local = new List<Option>();
        var global = new List<Option>();

        foreach (var opt in command.Options.Where(o => !o.Hidden))
        {
            if (seen.Add(opt.Name))
                local.Add(opt);
        }

        var parent = command.Parents.OfType<Command>().FirstOrDefault();
        while (parent is not null)
        {
            foreach (var opt in parent.Options.Where(o => !o.Hidden && o.Recursive))
            {
                if (seen.Add(opt.Name))
                    global.Add(opt);
            }
            parent = parent.Parents.OfType<Command>().FirstOrDefault();
        }

        return (local, global);
    }

    private static List<string> GetParentChain(Command command)
    {
        var chain = new List<string>();
        var current = command;
        while (current is not null and not RootCommand)
        {
            chain.Insert(0, current.Name);
            current = current.Parents.OfType<Command>().FirstOrDefault();
        }
        return chain;
    }

    private static string StyleOptionLabel(string label)
    {
        var valueStart = label.LastIndexOf(" <");
        if (valueStart < 0)
            return Styling.Option(label);

        var flags = label[..valueStart];
        var valueArg = label[valueStart..];
        return Styling.Option(flags) + " " + Styling.Value(valueArg[1..]);
    }

    private static string FormatOptionAliases(Option option)
    {
        var names = new List<string> { option.Name };
        foreach (var alias in option.Aliases)
            names.Add(alias);
        var sorted = names
            .OrderByDescending(n => n.StartsWith("--"))
            .ThenBy(n => n.Length)
            .ThenBy(n => n);
        var joined = string.Join(", ", sorted);

        if (option is { ValueType: not null } && option.ValueType != typeof(bool) && option.ValueType != typeof(bool?)
            && option is not HelpOption)
        {
            var valueName = option.Name.TrimStart('-');
            joined += $" <{valueName}>";
        }

        return joined;
    }
}
