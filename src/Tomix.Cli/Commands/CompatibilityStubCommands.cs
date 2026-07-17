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

    public static IReadOnlyList<CompatibilityStubCommand> All() => [];

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
