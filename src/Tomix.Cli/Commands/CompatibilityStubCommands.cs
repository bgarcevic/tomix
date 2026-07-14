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
