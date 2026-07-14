using System.CommandLine;

namespace Tomix.Cli.Commands;

internal sealed class CompatibilityStubCommand : ICommandModule
{
    private readonly string _name;
    private readonly string _description;
    private readonly IReadOnlyList<Argument> _arguments;
    private readonly IReadOnlyList<StubOption> _options;
    private readonly IReadOnlyList<CompatibilityStubCommand> _subcommands;

    private CompatibilityStubCommand(
        string name,
        string description,
        IReadOnlyList<Argument>? arguments = null,
        IReadOnlyList<StubOption>? options = null,
        IReadOnlyList<CompatibilityStubCommand>? subcommands = null)
    {
        _name = name;
        _description = description;
        _arguments = arguments ?? [];
        _options = options ?? [];
        _subcommands = subcommands ?? [];
    }

    public string Name => _name;

    public Command Build()
    {
        var command = new Command(_name, _description);

        foreach (var argument in _arguments)
            command.Arguments.Add(argument);

        foreach (var option in _options)
            command.Options.Add(option.Build());

        foreach (var subcommand in _subcommands)
            command.Subcommands.Add(subcommand.Build());

        command.SetAction(_ =>
        {
            Console.Error.WriteLine($"Command '{_name}' is not implemented yet.");
            return 1;
        });

        return command;
    }

    public static IReadOnlyList<CompatibilityStubCommand> All() =>
    [
        New("add", "Add an object to the model",
            args: [Required("path"), Optional("model")],
            options:
            [
                Value("--type", "-t"),
                Value("-i"),
                Value("-q"),
                Value("--file"),
                Flag("--if-not-exists"),
                Flag("--force"),
                Value("--save-to"),
                Value("--serialization"),
                Value("--mode"),
                Value("--source"),
                Value("--endpoint"),
                Value("--connection-string"),
                Value("--source-table"),
                Value("--source-database"),
                Value("--partition-expression"),
                Value("--columns"),
                Value("--source-type"),
                Flag("--stage"),
                Flag("--revert"),
                Flag("--save")
            ]),
        New("bpa", "Best Practice Analyzer: run rules and manage rule collections",
            subcommands:
            [
                New("rules", "Manage BPA rule collections"),
                New("run", "Run BPA rules", options: [Flag("--fix")])
            ]),
        New("connect", "Set active connection (workspace, local path, or PBI Desktop). No args = show current.",
            args: [Optional("server"), Optional("database")],
            options:
            [
                Value("--workspace"),
                Value("--profile"),
                Flag("--local"),
                Flag("--clear"),
                Flag("--force"),
                Value("--workspace-format"),
                Value("--workspace-auth")
            ]),
        New("diff", "Compare two semantic models and show structural differences. Exit codes: 0 = identical, 1 = differences found, 2 = error",
            args: [Required("left"), Required("right")]),
        New("format", "Format DAX or M/Power Query expressions (-e inline, -p object path, or all)",
            args: [Optional("model")],
            options:
            [
                Value("--expression", "-e"),
                Value("--path", "-p"),
                Value("--type", "-t"),
                Value("--lang"),
                Flag("--long"),
                Flag("--semicolons"),
                Flag("--no-space-after-function"),
                Flag("--stage"),
                Flag("--revert"),
                Flag("--save"),
                Value("--save-to")
            ]),
        New("incremental-refresh", "Configure incremental refresh policy on a table",
            subcommands:
            [
                New("apply", "Apply incremental refresh policy",
                    args: [Required("table")],
                    options:
                    [
                        Value("--effective-date")
                    ]),
                New("rm", "Remove incremental refresh policy",
                    args: [Required("table")],
                    options:
                    [
                        Flag("--force"),
                        Flag("--stage"),
                        Flag("--revert"),
                        Flag("--save")
                    ]),
                New("set", "Set incremental refresh policy",
                    args: [Required("table")],
                    options:
                    [
                        Value("--mode"),
                        Value("--rolling-window-periods"),
                        Value("--rolling-window-granularity"),
                        Value("--incremental-periods"),
                        Value("--incremental-granularity"),
                        Value("--incremental-offset"),
                        Value("--polling-expression"),
                        Value("--polling-expression-file"),
                        Value("--source-expression"),
                        Value("--source-expression-file"),
                        Flag("--force"),
                        Flag("--stage"),
                        Flag("--revert"),
                        Flag("--save"),
                        Value("--save-to")
                    ]),
                New("show", "Show incremental refresh policy",
                    args: [Required("table")])
            ]),
        New("init", "Create a new empty semantic model", args: [Optional("output-path")]),
        New("interactive", "Start an interactive REPL session for running multiple commands against a model",
            args: [Optional("model")]),
        New("mv", "Move or rename a model object",
            args: [Required("source"), Required("destination"), Optional("model")],
            options:
            [
                Flag("--force"),
                Value("--type", "-t"),
                Flag("--stage"),
                Flag("--revert"),
                Flag("--save"),
                Value("--save-to"),
                Value("--serialization")
            ]),
        New("profile", "Manage named connection profiles for quick environment switching",
            subcommands:
            [
                New("list", "List profiles"),
                New("remove", "Remove a profile"),
                New("set", "Set active profile"),
                New("show", "Show active profile")
            ]),
        New("query", "Execute a DAX query against a deployed model (-q inline, -f file, or stdin)",
            options:
            [
                Value("--query", "-q"),
                Value("--file"),
                Value("--limit"),
                Value("--output-file", "-o"),
                Flag("--no-validate"),
                Flag("--trace"),
                Flag("--cold"),
                Flag("--plan"),
                Value("--runs")
            ]),
        New("replace", "Find and replace text across model objects",
            args: [Optional("pattern"), Optional("replacement"), Optional("model")],
            options:
            [
                Value("--in"),
                Flag("--regex"),
                Flag("--case-sensitive"),
                Flag("--dry-run"),
                Flag("--force"),
                Flag("--stage"),
                Flag("--revert"),
                Flag("--save"),
                Value("--save-to"),
                Value("--serialization")
            ]),
        New("rm", "Remove an object from the model",
            args: [Required("path"), Optional("model")],
            options:
            [
                Flag("--force"),
                Flag("--dry-run"),
                Flag("--if-exists"),
                Value("--type", "-t"),
                Flag("--stage"),
                Flag("--revert"),
                Flag("--save"),
                Value("--save-to"),
                Value("--serialization")
            ]),
        New("save", "Save a model to disk in a specified format (like fab export)", args: [Optional("model")]),
        New("script", "Execute C# script(s) against a semantic model",
            args: [Optional("model")],
            options:
            [
                Value("--script", "-S"),
                Value("--expression", "-e"),
                Flag("--dry-run"),
                Flag("--force"),
                Flag("--stage"),
                Flag("--revert"),
                Flag("--save"),
                Value("--save-to"),
                Value("--serialization")
            ]),
        New("session", "Show or manage the current terminal session",
            subcommands:
            [
                New("clear", "Clear session state"),
                New("list", "List sessions"),
                New("prune", "Prune stale sessions"),
                New("show", "Show current session")
            ]),
        New("set", "Set a property on a model object",
            args: [Required("path"), Optional("model")],
            options:
            [
                Value("-q"),
                Value("-i"),
                Flag("--force"),
                Value("--type", "-t"),
                Flag("--stage"),
                Flag("--revert"),
                Flag("--save"),
                Value("--save-to"),
                Value("--serialization")
            ]),
        New("test", "Regression testing: DAX assertions, snapshots, and A/B model comparison",
            subcommands:
            [
                New("compare", "Compare test output",
                    options:
                    [
                        Value("--source-a", "-a"),
                        Value("--source-b", "-b"),
                        Value("--auth-a"),
                        Value("--auth-b"),
                        Value("--suite"),
                        Value("--tolerance")
                    ]),
                New("init", "Initialize test files",
                    options:
                    [
                        Value("--path"),
                        Value("--from-model"),
                        Value("--example")
                    ]),
                New("list", "List tests",
                    options:
                    [
                        Value("--suite"),
                        Value("--tag")
                    ]),
                New("run", "Run tests",
                    options:
                    [
                        Value("--suite"),
                        Value("--tag"),
                        Value("--fail-on"),
                        Flag("--ci"),
                        Value("--trx")
                    ]),
                New("snapshot", "Manage snapshots",
                    options:
                    [
                        Value("--suite"),
                        Value("--table"),
                        Value("--measures"),
                        Value("--tolerance"),
                        Flag("--save"),
                        Flag("--diff")
                    ]),
                New("spec", "Show test specification",
                    options:
                    [
                        Flag("--json-schema")
                    ]),
                New("use", "Select test profile",
                    args: [Optional("suite")])
            ]),
        New("validate", "Validate DAX expressions and relationship integrity (--ci for CI output, --trx for VSTEST)",
            args: [Optional("model")],
            options:
            [
                Flag("--ci"),
                Value("--trx"),
                Flag("--errors-only"),
                Flag("--no-warnings"),
                Flag("--no-antipatterns"),
                Flag("--no-multiline"),
                Flag("--server-only")
            ]),
        New("vertipaq", "Analyze VertiPaq storage statistics for a semantic model",
            args: [Optional("path")],
            options:
            [
                Value("--import"),
                Value("--export"),
                Flag("--obfuscate"),
                Flag("--columns"),
                Flag("--relationships"),
                Flag("--partitions"),
                Flag("--all"),
                Flag("--detail"),
                Value("--top"),
                Flag("--stats"),
                Flag("--annotate"),
                Flag("--save"),
                Value("--fields")
            ])
    ];

    private static CompatibilityStubCommand New(
        string name,
        string description,
        IReadOnlyList<Argument>? args = null,
        IReadOnlyList<StubOption>? options = null,
        IReadOnlyList<CompatibilityStubCommand>? subcommands = null)
        => new(name, description, args, options, subcommands);

    private static Argument<string> Required(string name) => new(name);

    private static Argument<string?> Optional(string name)
        => new(name) { Arity = ArgumentArity.ZeroOrOne };

    private static StubOption Value(string name, string? alias = null) => new(name, alias, IsFlag: false);

    private static StubOption Flag(string name, string? alias = null) => new(name, alias, IsFlag: true);

    private sealed record StubOption(string Name, string? Alias, bool IsFlag)
    {
        public Option Build()
        {
            Option option = IsFlag ? new Option<bool>(Name) : new Option<string?>(Name);
            if (!string.IsNullOrWhiteSpace(Alias))
                option.Aliases.Add(Alias);
            return option;
        }
    }
}
