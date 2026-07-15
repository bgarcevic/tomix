using System.CommandLine;
using Tomix.App.Format;
using Tomix.Cli.Commands;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

/// <summary>
/// Live-model QA finding: commands without a renderer for a format silently fell back to text
/// when given <c>--output-format csv</c> or <c>tmdl</c> (only ls/find/deps validated). Every
/// command must declare its supported formats and reject the rest with exit 2 before touching
/// any model or state.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class OutputFormatRejectionTests
{
    private static readonly IReadOnlyList<IModelProvider> NoProviders = [];

    private static Command BuildModule(string name)
        => (name switch
        {
            "add" => new AddCommand(NoProviders),
            "auth" => new AuthCommand(),
            "bpa" => new BpaCommand(NoProviders),
            "config" => new ConfigCommand(),
            "connect" => new ConnectCommand(NoProviders, FakeWorkspaceCatalog.Empty, () => null),
            "deploy" => new DeployCommand(NoProviders),
            "diff" => new DiffCommand(NoProviders),
            "doctor" => new DoctorCommand("0.0.0-test"),
            "format" => new FormatCommand(NoProviders, new CompositeExpressionFormatterClient([])),
            "get" => new GetCommand(NoProviders),
            "init" => new InitCommand(),
            "interactive" => new InteractiveCommand(),
            "load" => new LoadCommand(NoProviders),
            "profile" => new ProfileCommand(),
            "refresh" => new RefreshCommand(NoProviders),
            "replace" => new ReplaceCommand(NoProviders),
            "rm" => new RmCommand(NoProviders),
            "save" => new SaveCommand(NoProviders),
            "script" => new ScriptCommand(NoProviders),
            "session" => new SessionCommand(),
            "set" => new SetCommand(NoProviders),
            "stage" => new StageCommand(NoProviders),
            "validate" => new ValidateCommand(NoProviders),
            "vertipaq" => new VertipaqCommand(
                NoProviders, new Tomix.Provider.Vpax.VpaxVertipaqAnalyzer(tokenProvider: null, "0.0.0-test")),
            _ => (ICommandModule?)null,
        })?.Build() ?? throw new ArgumentException($"Unknown module '{name}'.");

    private static (int ExitCode, string Stderr, ParseResult Result) Invoke(params string[] args)
    {
        var root = new RootCommand("test");
        foreach (var option in GlobalOptions.All())
            root.Options.Add(option);
        root.Subcommands.Add(BuildModule(args[0]));

        var result = root.Parse(args);
        var original = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            return (result.Invoke(), stderr.ToString(), result);
        }
        finally
        {
            Console.SetError(original);
        }
    }

    // Every command that renders text/json only, probed with csv (the silent-fallback repro),
    // plus the csv-capable commands probed with tmdl. Arguments are minimal valid parses;
    // rejection must happen before any model, session, or config state is touched.
    [Theory]
    [InlineData("csv", "add", "tables/T/measures/M")]
    [InlineData("csv", "auth", "status")]
    [InlineData("csv", "bpa", "run")]
    [InlineData("csv", "config", "show")]
    [InlineData("csv", "connect")]
    [InlineData("csv", "deploy")]
    [InlineData("csv", "diff", "left", "right")]
    [InlineData("csv", "doctor")]
    [InlineData("csv", "format")]
    [InlineData("csv", "init")]
    [InlineData("csv", "interactive")]
    [InlineData("csv", "load")]
    [InlineData("csv", "profile", "list")]
    [InlineData("csv", "replace", "old", "new")]
    [InlineData("csv", "rm", "tables/T/measures/M")]
    [InlineData("csv", "session")]
    [InlineData("csv", "set", "tables/T/measures/M")]
    [InlineData("csv", "stage")]
    [InlineData("csv", "validate")]
    [InlineData("tmdl", "refresh")]
    [InlineData("tmdl", "save")]
    [InlineData("tmdl", "script", "-e", "1")]
    [InlineData("tmdl", "vertipaq")]
    public void UnsupportedFormat_ExitsTwoWithMessage(string format, params string[] commandArgs)
    {
        var (exitCode, stderr, result) = Invoke([.. commandArgs, "--output-format", format]);

        Assert.Empty(result.Errors);
        Assert.Equal(2, exitCode);
        Assert.Contains($"does not support --output-format {format}", stderr);
    }

    [Theory]
    [InlineData("tmdl", "get", "model")]
    [InlineData("csv", "get", "model")]
    public void SupportedFormat_PassesValidation(string format, params string[] commandArgs)
    {
        var (_, stderr, result) = Invoke([.. commandArgs, "--output-format", format]);

        Assert.Empty(result.Errors);
        Assert.DoesNotContain("does not support --output-format", stderr);
    }
}
