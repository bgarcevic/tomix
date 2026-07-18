using System.CommandLine;
using Tomix.Cli.Commands;
using Tomix.Core.Models;
using Tomix.Core.Vertipaq;

namespace Tomix.Cli.Tests;

/// <summary>
/// Parse-time and pre-flight validation for <c>vertipaq</c>: bad --top/--fields values, csv
/// with multiple views, and option conflicts all exit 2 before any model or connection is
/// touched. Uses a throwing analyzer to prove nothing was analyzed.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class VertipaqCommandParseTests
{
    private static Command BuildCommand()
        => new VertipaqCommand([], new ThrowingAnalyzer(), TestServices.Create()).Build();

    private static ParseResult Parse(params string[] args)
    {
        var root = new RootCommand("test");
        foreach (var option in GlobalOptions.All())
            root.Options.Add(option);
        root.Subcommands.Add(BuildCommand());
        return root.Parse(args);
    }

    private static (int ExitCode, string Stderr) Invoke(params string[] args)
    {
        var result = Parse(args);
        Assert.Empty(result.Errors);

        var original = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            return (result.Invoke(), stderr.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }

    [Fact]
    public void AllDocumentedOptions_ParseCleanly()
    {
        var result = Parse(
            "vertipaq", "Sales", "--tables", "--columns", "--relationships", "--partitions",
            "--all", "--detail", "--fields", "name,size", "--top", "5", "--stats",
            "--annotate", "--save", "--export", "out.vpax", "--obfuscate");

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Top_NonInteger_FailsAtParseTime()
    {
        var result = Parse("vertipaq", "--top", "abc");

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Top_Zero_ExitsTwo()
    {
        var (exitCode, stderr) = Invoke("vertipaq", "--top", "0", "--quiet");

        Assert.Equal(2, exitCode);
        Assert.Contains("--top must be a positive integer", stderr);
    }

    [Fact]
    public void Fields_UnknownToken_ExitsTwo_ListingValidTokens()
    {
        var (exitCode, stderr) = Invoke("vertipaq", "--fields", "name,bogus", "--quiet");

        Assert.Equal(2, exitCode);
        Assert.Contains("bogus", stderr);
        Assert.Contains("card", stderr); // the valid-token list for the columns view
    }

    [Fact]
    public void Fields_WithMultipleViews_ExitsTwo()
    {
        var (exitCode, stderr) = Invoke("vertipaq", "--all", "--fields", "name,size", "--quiet");

        Assert.Equal(2, exitCode);
        Assert.Contains("single view", stderr);
    }

    [Fact]
    public void Csv_WithMultipleViews_ExitsTwo()
    {
        var (exitCode, stderr) = Invoke("vertipaq", "--all", "--output-format", "csv", "--quiet");

        Assert.Equal(2, exitCode);
        Assert.Contains("single view", stderr);
    }

    [Fact]
    public void Obfuscate_WithoutExport_ExitsTwo()
    {
        var (exitCode, stderr) = Invoke("vertipaq", "--obfuscate", "--quiet");

        Assert.Equal(2, exitCode);
        Assert.Contains("--obfuscate requires --export", stderr);
    }

    [Fact]
    public void Save_WithoutAnnotate_ExitsTwo()
    {
        var (exitCode, stderr) = Invoke("vertipaq", "--save", "--quiet");

        Assert.Equal(2, exitCode);
        Assert.Contains("--save", stderr);
    }

    private sealed class ThrowingAnalyzer : IVertipaqAnalyzer
    {
        public Task<VertipaqModelStats> AnalyzeAsync(ModelReference model, CancellationToken _)
            => throw new InvalidOperationException("The analyzer must not be reached by validation tests.");

        public Task<VertipaqModelStats> ImportAsync(string vpaxPath, CancellationToken _)
            => throw new InvalidOperationException("The analyzer must not be reached by validation tests.");

        public Task<VertipaqExportResult> ExportAsync(
            ModelReference model, string vpaxPath, bool obfuscate, CancellationToken _)
            => throw new InvalidOperationException("The analyzer must not be reached by validation tests.");
    }
}
