using System.Text.Json;
using Spectre.Console;
using Tomix.Cli.Commands;

namespace Tomix.Cli.Tests;

[Collection(ConsoleStateCollection.Name)]
public sealed class DoctorAndConfigRecoveryTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("tomix-recovery-tests").FullName;
    private readonly string? _originalConfigDirectory = Environment.GetEnvironmentVariable("TOMIX_CONFIG_DIR");

    public DoctorAndConfigRecoveryTests()
    {
        Environment.SetEnvironmentVariable("TOMIX_CONFIG_DIR", _directory);
        File.WriteAllText(Path.Combine(_directory, "config.json"), "not json");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TOMIX_CONFIG_DIR", _originalConfigDirectory);
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Doctor_RunsWithCorruptConfigAndReportsFailureAsJson()
    {
        var invocation = Invoke("doctor", "--output-format", "json");

        Assert.Equal(1, invocation.ExitCode);
        using var json = JsonDocument.Parse(invocation.Stdout);
        Assert.True(json.RootElement.TryGetProperty("terminal", out _));
        Assert.Contains(json.RootElement.GetProperty("checks").EnumerateArray(), check =>
            check.GetProperty("name").GetString() == "configuration" &&
            check.GetProperty("status").GetString() == "Fail");
    }

    [Fact]
    public void ConfigPaths_RunsWithCorruptConfig()
    {
        var invocation = Invoke("config", "paths", "--output-format", "json");

        Assert.Equal(0, invocation.ExitCode);
        using var json = JsonDocument.Parse(invocation.Stdout);
        Assert.Equal(_directory, json.RootElement.GetProperty("configDir").GetString());
    }

    [Fact]
    public void ConfigInitForce_RepairsCorruptConfig()
    {
        var invocation = Invoke("config", "init", "--force");

        Assert.Equal(0, invocation.ExitCode);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, "config.json")));
        Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
    }

    [Fact]
    public void OtherCommands_KeepStructuredConfigCorruptFailure()
    {
        var invocation = Invoke("session", "--error-format", "json");

        Assert.Equal(2, invocation.ExitCode);
        Assert.Contains("\"code\": \"TOMIX_CONFIG_CORRUPT\"", invocation.Stderr);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--version")]
    public void HelpAndVersion_RunWithCorruptConfig(string option)
    {
        var invocation = Invoke(option);

        Assert.Equal(0, invocation.ExitCode);
        Assert.DoesNotContain("TOMIX_CONFIG_CORRUPT", invocation.Stderr);
    }

    [Fact]
    public void SubcommandVersion_DoesNotBypassCorruptConfigFailure()
    {
        var invocation = Invoke("update", "--version", "1.2.3", "--yes", "--error-format", "json");

        Assert.Equal(2, invocation.ExitCode);
        Assert.Contains("\"code\": \"TOMIX_CONFIG_CORRUPT\"", invocation.Stderr);
    }

    private static Invocation Invoke(params string[] args)
    {
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
            return new Invocation(Program.Run(args), stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            AnsiConsole.Console = originalAnsiConsole;
        }
    }

    private sealed record Invocation(int ExitCode, string Stdout, string Stderr);
}

[Collection(ConsoleStateCollection.Name)]
public sealed class ConfigDefaultFormatIntegrationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("tomix-format-tests").FullName;
    private readonly string? _originalConfigDirectory = Environment.GetEnvironmentVariable("TOMIX_CONFIG_DIR");

    public ConfigDefaultFormatIntegrationTests()
    {
        Environment.SetEnvironmentVariable("TOMIX_CONFIG_DIR", _directory);
        File.WriteAllText(Path.Combine(_directory, "config.json"), "{ \"defaultFormat\": \"json\" }");
    }

    public void Dispose()
    {
        GlobalOptions.ConfigureDefaultOutputFormat("text");
        Environment.SetEnvironmentVariable("TOMIX_CONFIG_DIR", _originalConfigDirectory);
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void ConfiguredDefaultAppliesAndExplicitOutputFormatWins()
    {
        var implicitResult = Invoke("config", "paths");
        using var json = JsonDocument.Parse(implicitResult.Stdout);
        Assert.Equal(_directory, json.RootElement.GetProperty("configDir").GetString());

        var explicitResult = Invoke("config", "paths", "--output-format", "text");
        Assert.Equal(0, explicitResult.ExitCode);
        Assert.Contains("configDir", explicitResult.Stdout);
    }

    [Fact]
    public void CompletionRemainsTextUnderJsonDefault()
    {
        var result = Invoke("completion", "zsh");

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("# tomix shell completion", result.Stdout);
    }

    private static Invocation Invoke(params string[] args)
    {
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
            return new Invocation(Program.Run(args), stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            AnsiConsole.Console = originalAnsiConsole;
        }
    }

    private sealed record Invocation(int ExitCode, string Stdout, string Stderr);
}
