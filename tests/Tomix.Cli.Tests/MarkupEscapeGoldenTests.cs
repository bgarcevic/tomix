using System.Text.RegularExpressions;
using Spectre.Console;
using Tomix.Cli.Commands;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;

namespace Tomix.Cli.Tests;

/// <summary>Human command output keeps bracket-bearing model identifiers literal.</summary>
[Collection(ConsoleStateCollection.Name)]
public sealed partial class MarkupEscapeGoldenTests
{
    private const string TableName = "[Test] [Table]";
    private const string ColumnName = "[Test] [Column]";
    private const string MeasureName = "[Test] [Measure]";
    private const string RelationshipName = "[Test] [Relationship]";

    private static readonly IReadOnlyList<IModelProvider> Providers = [new TmdlModelProvider()];

    [Theory]
    [InlineData("ls")]
    [InlineData("ls-children")]
    [InlineData("get")]
    [InlineData("get-measure")]
    [InlineData("find")]
    [InlineData("diff")]
    public void BracketBearingModelNames_RenderLiterally(string command)
    {
        using var copy = SampleModel.CopyToTemp();
        AddBracketedTable(copy.Path);

        var services = TestServices.Create();
        var root = TestRoot.With(
            new LsCommand(Providers, services.State).Build(),
            new GetCommand(Providers, services.State).Build(),
            new FindCommand(Providers, services.State).Build(),
            new DiffCommand(Providers).Build());
        string[] args = command switch
        {
            "ls" => ["ls", copy.Path],
            "ls-children" => ["ls", TableName, copy.Path],
            "get" => ["get", TableName, copy.Path],
            "get-measure" => ["get", $"{TableName}/{MeasureName}", copy.Path],
            "find" => ["find", "Test", copy.Path, "--in", "names"],
            "diff" => ["diff", SampleModel.Locate(), copy.Path],
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };

        var captured = ConsoleCapture.Run(
            () => InvokeAtFixedWidth(() => root.Parse(args).Invoke()),
            captureAnsiConsole: true,
            forceAnsi: true);
        Assert.True(captured.ExitCode is 0 or 1,
            $"{command} exited {captured.ExitCode}: {captured.Stderr}");

        var visible = Visible(captured.Stdout, copy.Path);
        Assert.Contains(TableName, visible);
        Assert.DoesNotContain("[[Test]]", visible);
        if (command == "find")
        {
            Assert.Contains(ColumnName, visible);
            Assert.Contains(MeasureName, visible);
        }
        if (command == "diff")
        {
            Assert.Contains(RelationshipName, visible);
        }
        if (command is "get-measure" or "ls-children")
        {
            Assert.Contains("SUM(Sales[Amount])", visible);
            Assert.Contains("\x1b[38;2;64;129;57m[Amount]", captured.Stdout);
        }

        AssertGolden(command, visible);
    }

    [Fact]
    public void Find_NoMatch_RendersBracketedPatternOnce()
    {
        using var copy = SampleModel.CopyToTemp();
        var services = TestServices.Create();
        var root = TestRoot.With(new FindCommand(Providers, services.State).Build());

        var captured = ConsoleCapture.Run(
            () => InvokeAtFixedWidth(() => root.Parse(["find", "[Missing]", copy.Path]).Invoke()),
            captureAnsiConsole: true,
            forceAnsi: true);

        Assert.Equal(0, captured.ExitCode);
        var visible = Visible(captured.Stdout, copy.Path);
        Assert.Contains("No matches for '[Missing]'.", visible);
        AssertGolden("find-no-match", visible);
        // The "Try" hint is commentary (#255): stderr, with the pattern still literal.
        Assert.Contains("→ Try:", Visible(captured.Stderr, copy.Path));
    }

    [Fact]
    public void Diff_ModifiedExpression_RendersOldAndNewBracketReferences()
    {
        using var original = SampleModel.CopyToTemp();
        using var modified = SampleModel.CopyToTemp();
        var tablePath = Path.Combine(modified.Path, "tables", "Sales.tmdl");
        File.WriteAllText(tablePath, File.ReadAllText(tablePath).Replace(
            "SUM ( Sales[Amount] )",
            "SUM ( Sales[Amount] ) + [Order Count]",
            StringComparison.Ordinal));

        var root = TestRoot.With(new DiffCommand(Providers).Build());
        var captured = ConsoleCapture.Run(
            () => InvokeAtFixedWidth(() => root.Parse(["diff", original.Path, modified.Path]).Invoke()),
            captureAnsiConsole: true,
            forceAnsi: true);

        Assert.Equal(1, captured.ExitCode);
        var visible = Visible(captured.Stdout, original.Path, modified.Path);
        Assert.Contains("SUM ( Sales[Amount] )", visible);
        Assert.Contains("SUM ( Sales[Amount] ) + [Order Count]", visible);
        AssertGolden("diff-modified", visible);
    }

    [Theory]
    [InlineData("ls", "json")]
    [InlineData("ls", "csv")]
    [InlineData("get", "json")]
    [InlineData("get", "csv")]
    [InlineData("get", "tmdl")]
    [InlineData("get", "bim")]
    [InlineData("find", "json")]
    [InlineData("diff", "json")]
    public void MachineOutput_PreservesLiteralNames(string command, string format)
    {
        using var copy = SampleModel.CopyToTemp();
        AddBracketedTable(copy.Path);
        var services = TestServices.Create();
        var root = TestRoot.With(
            new LsCommand(Providers, services.State).Build(),
            new GetCommand(Providers, services.State).Build(),
            new FindCommand(Providers, services.State).Build(),
            new DiffCommand(Providers).Build());
        string[] args = command switch
        {
            "ls" => ["ls", copy.Path, "--output-format", format],
            "get" => ["get", TableName, copy.Path, "--output-format", format],
            "find" => ["find", "Test", copy.Path, "--in", "names", "--output-format", format],
            "diff" => ["diff", SampleModel.Locate(), copy.Path, "--output-format", format],
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };

        var captured = ConsoleCapture.Run(() => root.Parse(args).Invoke());
        Assert.True(captured.ExitCode is 0 or 1, captured.Stderr);
        Assert.Contains(TableName, captured.Stdout);
        Assert.DoesNotContain("[[Test]]", captured.Stdout);
        Assert.DoesNotContain('\x1b', captured.Stdout);
    }

    private static string Visible(string output, params string[] paths)
    {
        var visible = AnsiRegex().Replace(output, "").ReplaceLineEndings("\n")
            .Replace(SampleModel.Locate(), "<SAMPLE>", StringComparison.Ordinal);
        for (var i = 0; i < paths.Length; i++)
            visible = visible.Replace(paths[i], $"<MODEL_{i + 1}>", StringComparison.Ordinal);
        return TrailingWhitespaceRegex().Replace(visible, "");
    }

    private static int InvokeAtFixedWidth(Func<int> invoke)
    {
        AnsiConsole.Profile.Width = 160;
        return invoke();
    }

    private static void AssertGolden(string name, string actual)
    {
        var path = RepoPaths.Combine("tests", "Tomix.Cli.Tests", "Goldens", $"{name}.approved.txt");
        if (Environment.GetEnvironmentVariable("TOMIX_UPDATE_MARKUP_GOLDENS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Missing render golden: {path}");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    private static void AddBracketedTable(string modelPath)
    {
        File.AppendAllText(Path.Combine(modelPath, "model.tmdl"),
            "\nref table '[Test] [Table]'\n");
        File.WriteAllText(Path.Combine(modelPath, "tables", "[Test] [Table].tmdl"),
            "table '[Test] [Table]'\n\n" +
            "\tmeasure '[Test] [Measure]' = SUM(Sales[Amount])\n\n" +
            "\tcolumn '[Test] [Column]'\n" +
            "\t\tdataType: decimal\n" +
            "\t\tsourceColumn: Amount\n\n" +
            "\tpartition '[Test] [Table]' = m\n" +
            "\t\tmode: import\n" +
            "\t\tsource = let Source = #table({\"Amount\"}, {}) in Source\n");
        File.AppendAllText(Path.Combine(modelPath, "relationships.tmdl"),
            "\nrelationship '[Test] [Relationship]'\n" +
            "\tfromColumn: Sales.CustomerID\n" +
            "\ttoColumn: Customers.CustomerID\n");
    }

    [GeneratedRegex("\x1b\\[[0-9;]*m")]
    private static partial Regex AnsiRegex();

    [GeneratedRegex("[ \\t]+(?=\\n|$)")]
    private static partial Regex TrailingWhitespaceRegex();
}
