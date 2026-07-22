using System.CommandLine;
using System.Text.Json;
using Spectre.Console;
using Tomix.Cli.Commands;

namespace Tomix.Cli.Tests;

[Collection(ConsoleStateCollection.Name)]
public sealed class CompletionAndConfigOutputTests
{
    [Fact]
    public void Completion_MissingShell_UsesStructuredErrorAndExitTwo()
    {
        var result = Invoke(new CompletionCommand().Build(), "completion", "--error-format", "json");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("\"code\": \"TOMIX_COMPLETION_SHELL_REQUIRED\"", result.Stderr);
    }

    [Fact]
    public void Completion_UnsupportedShell_UsesStructuredErrorAndExitTwo()
    {
        var result = Invoke(new CompletionCommand().Build(), "completion", "tcsh", "--error-format", "json");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("\"code\": \"TOMIX_COMPLETION_UNSUPPORTED_SHELL\"", result.Stderr);
    }

    [Fact]
    public void Completion_RejectsExplicitJsonOutput()
    {
        var result = Invoke(new CompletionCommand().Build(), "completion", "bash", "--output-format", "json");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("does not support --output-format json", result.Stderr);
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public void Completion_RejectsExplicitJsonOutputBeforeCommandName()
    {
        var result = Invoke(new CompletionCommand().Build(), "--output-format", "json", "completion", "bash");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("does not support --output-format json", result.Stderr);
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public void Completion_SupportedShellWritesTextScript()
    {
        var result = Invoke(new CompletionCommand().Build(), "completion", "zsh");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("tx completion zsh", result.Stdout);
    }

    [Fact]
    public void ConfigPaths_JsonHasStableShape()
    {
        var services = TestServices.Create();
        var result = Invoke(
            new ConfigCommand(services.ConfigStore, services.ConfigDirectory, services.ConfigFilePath).Build(),
            "config", "paths", "--output-format", "json");

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.Equal(services.ConfigDirectory, json.RootElement.GetProperty("configDir").GetString());
        Assert.Equal(services.ConfigFilePath, json.RootElement.GetProperty("configFile").GetString());
    }

    [Fact]
    public void ConfigShow_MarksPreservedUnsupportedKeys()
    {
        var services = TestServices.Create();
        services.ConfigStore.Save(new Dictionary<string, string>
        {
            ["telemetry"] = "false"
        });

        var result = Invoke(
            new ConfigCommand(services.ConfigStore, services.ConfigDirectory, services.ConfigFilePath).Build(),
            "config", "show");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("telemetry", result.Stdout);
        Assert.Contains("(unsupported)", result.Stdout);
    }

    [Fact]
    public void ConfiguredJsonDefaultAppliesButExplicitTextWins()
    {
        var services = TestServices.Create();
        var command = new ConfigCommand(services.ConfigStore, services.ConfigDirectory, services.ConfigFilePath).Build();
        GlobalOptions.ConfigureDefaultOutputFormat("json");
        try
        {
            var implicitResult = Invoke(command, "config", "paths");
            Assert.Equal(0, implicitResult.ExitCode);
            using var json = JsonDocument.Parse(implicitResult.Stdout);
            Assert.Equal(services.ConfigDirectory, json.RootElement.GetProperty("configDir").GetString());

            var explicitResult = Invoke(
                new ConfigCommand(services.ConfigStore, services.ConfigDirectory, services.ConfigFilePath).Build(),
                "config", "paths", "--output-format", "text");
            Assert.Contains("configDir", explicitResult.Stdout);
        }
        finally
        {
            GlobalOptions.ConfigureDefaultOutputFormat("text");
        }
    }

    private static InvocationResult Invoke(Command command, params string[] args)
    {
        var root = new RootCommand("test");
        foreach (var option in GlobalOptions.All())
            root.Options.Add(option);
        root.Subcommands.Add(command);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var originalAnsiConsole = AnsiConsole.Console;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(stdout)
        });
        try
        {
            var parsed = root.Parse(args);
            return new InvocationResult(parsed.Invoke(), stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            AnsiConsole.Console = originalAnsiConsole;
        }
    }

    private sealed record InvocationResult(int ExitCode, string Stdout, string Stderr);
}
