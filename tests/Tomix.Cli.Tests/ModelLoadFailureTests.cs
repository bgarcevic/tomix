using System.CommandLine;
using Tomix.Cli;
using Tomix.Cli.Commands;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

/// <summary>
/// Live-model QA finding: a model that fails to load (e.g. TMDL with unresolvable references)
/// crashed every command with a raw provider stack trace instead of a diagnostic. Provider load
/// errors must surface as <c>TOMIX_MODEL_LOAD_FAILED</c> with exit 2.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class ModelLoadFailureTests
{
    [Fact]
    public void ModelLoadException_IsReportedAsDiagnostic_WithExitTwo()
    {
        var (exitCode, stderr) = Invoke("ls", "broken-model");

        Assert.Equal(2, exitCode);
        Assert.Contains("Cannot load TMDL model", stderr);
        Assert.DoesNotContain("at Tomix", stderr); // no stack trace
    }

    [Fact]
    public void ModelLoadException_UsesJsonEnvelope_WhenErrorFormatJson()
    {
        var (exitCode, stderr) = Invoke("ls", "broken-model", "--error-format", "json");

        Assert.Equal(2, exitCode);
        Assert.Contains("\"code\": \"TOMIX_MODEL_LOAD_FAILED\"", stderr);
    }

    [Fact]
    public void UnexpectedException_StillReportsAndExitsOne()
    {
        var (exitCode, stderr) = Invoke("ls", "explode");

        Assert.Equal(1, exitCode);
        Assert.Contains("Unexpected error: boom", stderr);
        Assert.DoesNotContain("at Tomix", stderr); // no stack trace without --debug
    }

    [Fact]
    public void UnexpectedException_UsesJsonEnvelope_WhenErrorFormatJson()
    {
        var (exitCode, stderr) = Invoke("ls", "explode", "--error-format", "json");

        Assert.Equal(1, exitCode);
        Assert.Contains("\"code\": \"TOMIX_UNEXPECTED\"", stderr);
        Assert.DoesNotContain("at Tomix", stderr);
    }

    [Fact]
    public void UnexpectedException_PrintsStackTrace_UnderDebug()
    {
        var (exitCode, stderr) = Invoke("ls", "explode", "--debug");

        Assert.Equal(1, exitCode);
        Assert.Contains("Unexpected error: boom", stderr);
        Assert.Contains("InvalidOperationException", stderr); // full exception under --debug
    }

    [Fact]
    public void UnexpectedException_DebugWithJsonErrorFormat_KeepsStderrValidJson()
    {
        var (exitCode, stderr) = Invoke("ls", "explode", "--error-format", "json", "--debug");

        Assert.Equal(1, exitCode);
        // The stack trace rides inside the envelope's "detail" field; stderr must stay
        // one parseable JSON document for automation even in the debugging scenario.
        using var doc = System.Text.Json.JsonDocument.Parse(stderr);
        Assert.Equal("TOMIX_UNEXPECTED", doc.RootElement.GetProperty("code").GetString());
        Assert.Contains("InvalidOperationException", doc.RootElement.GetProperty("detail").GetString());
    }

    private static (int ExitCode, string Stderr) Invoke(params string[] args)
    {
        var root = new RootCommand("test");
        foreach (var option in GlobalOptions.All())
            root.Options.Add(option);
        root.Subcommands.Add(new LsCommand([new ThrowingProvider()], TestServices.Create()).Build());

        var parseResult = root.Parse(args);
        var original = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            return (Program.Invoke(parseResult), stderr.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }

    /// <summary>Fails at session-open the way a corrupt on-disk model does.</summary>
    private sealed class ThrowingProvider : IModelProvider
    {
        public bool CanOpen(ModelReference _) => true;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken ct)
            => reference.Value.Contains("explode")
                ? throw new InvalidOperationException("boom")
                : throw new ModelLoadException(
                    $"Cannot load TMDL model from '{reference.Value}': unresolvable reference.",
                    new InvalidOperationException("inner"));
    }
}
