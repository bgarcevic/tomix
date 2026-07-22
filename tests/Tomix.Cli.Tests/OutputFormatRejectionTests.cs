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
    {
        var services = TestServices.Create();
        var mutations = services.Mutations;

        return (name switch
        {
            "add" => new AddCommand(NoProviders, services.State, mutations),
            "auth" => new AuthCommand(services.ConfigStore, services.State),
            "bpa" => new BpaCommand(
                NoProviders, services.State, mutations, services.BpaRules, services.ConfigDirectory),
            "config" => new ConfigCommand(services.ConfigStore, services.ConfigDirectory, services.ConfigFilePath),
            "connect" => new ConnectCommand(NoProviders, FakeWorkspaceCatalog.Empty, () => null, services.State),
            "deploy" => new DeployCommand(NoProviders, services.State),
            "diff" => new DiffCommand(NoProviders),
            "doctor" => new DoctorCommand(
                "0.0.0-test", services.ConfigDirectory, services.ConfigStore, services.State,
                services.UpdateCheck, Path.Combine(services.ConfigDirectory, "auth", "auth-state.json"),
                ["FakeProvider"]),
            "format" => new FormatCommand(NoProviders, new CompositeExpressionFormatterClient([]), services.State, mutations),
            "get" => new GetCommand(NoProviders, services.State),
            "init" => new InitCommand(),
            "load" => new LoadCommand(NoProviders, services.State),
            "profile" => new ProfileCommand(services.State),
            "refresh" => new RefreshCommand(NoProviders, services.State, services.LoadCurrentSession),
            "replace" => new ReplaceCommand(NoProviders, services.State, mutations),
            "rm" => new RmCommand(NoProviders, services.State, mutations),
            "save" => new SaveCommand(NoProviders, services.State),
            "script" => new ScriptCommand(NoProviders, services.State, mutations),
            "session" => new SessionCommand(services.State),
            "set" => new SetCommand(NoProviders, services.State, mutations),
            "stage" => new StageCommand(NoProviders, services.State, services.Staging),
            "update" => new UpdateCommand("0.0.0-test", FakeReleaseSource.Empty, services.UpdateCheck),
            "validate" => new ValidateCommand(NoProviders, services.State),
            "vertipaq" => new VertipaqCommand(
                NoProviders,
                new Tomix.Provider.Vpax.VpaxVertipaqAnalyzer(tokenProvider: null, "0.0.0-test"),
                services.State,
                mutations),
            _ => (ICommandModule?)null,
        })?.Build() ?? throw new ArgumentException($"Unknown module '{name}'.");
    }

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
    [InlineData("csv", "load")]
    [InlineData("csv", "profile", "list")]
    [InlineData("csv", "replace", "old", "new")]
    [InlineData("csv", "rm", "tables/T/measures/M")]
    [InlineData("csv", "session")]
    [InlineData("csv", "set", "tables/T/measures/M")]
    [InlineData("csv", "stage")]
    [InlineData("csv", "update", "--check")]
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

    [Fact]
    public void UnsupportedFormat_UsesJsonEnvelope_WhenErrorFormatJson()
    {
        var (exitCode, stderr, _) = Invoke("session", "--output-format", "csv", "--error-format", "json");

        Assert.Equal(2, exitCode);
        Assert.Contains("\"code\": \"TOMIX_OUTPUT_FORMAT_UNSUPPORTED\"", stderr);
    }

    [Fact]
    public void InvalidFormat_UsesJsonEnvelope_WhenErrorFormatJson()
    {
        var (exitCode, stderr, _) = Invoke("session", "--output-format", "yaml", "--error-format", "json");

        Assert.Equal(2, exitCode);
        Assert.Contains("\"code\": \"TOMIX_INVALID_OUTPUT_FORMAT\"", stderr);
    }
}
