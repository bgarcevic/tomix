using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tomix.App;
using Tomix.App.Format;
using Tomix.Cli.Commands;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;
using Tomix.Provider.Tom;
using Tomix.Tests.Support;

namespace Tomix.Cli.Tests;

/// <summary>
/// <c>tx interactive</c> driven through redirected input, the way scripts and CI run it (#347):
/// each line runs against one live session, edits stay in memory until <c>save</c>, and leaving
/// with unsaved work needs <c>--discard-on-exit</c> or <c>--yes</c>.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed partial class InteractiveCommandTests
{
    private static readonly IReadOnlyList<IModelProvider> Providers = [new TmdlModelProvider(), new TomFileModelProvider()];

    [Fact]
    public void Script_EditsUndoesRedoesAndSaves()
    {
        using var model = SampleModel.CopyToTemp();

        var run = Run(model.Path, """
            add Sales/Margin -t Measure -e "1"
            set Sales/Margin -p formatString="0.0%"
            undo
            redo
            save
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Added: Sales/Margin", run.Stdout);
        Assert.Contains("Undone: set Sales/Margin -p formatString=\"0.0%\" (1 change)", run.Stdout);
        Assert.Contains("Redone: set Sales/Margin -p formatString=\"0.0%\" (1 change)", run.Stdout);
        var sales = SalesTable(model.Path);
        Assert.Contains("measure Margin = 1", sales);
        Assert.Contains("formatString: 0.0%", sales);
    }

    [Fact]
    public void Edits_StayInMemoryUntilSave()
    {
        using var model = SampleModel.CopyToTemp();
        var before = SalesTable(model.Path);

        var run = Run(model.Path, """
            add Sales/Margin -t Measure -e "1"
            get Sales/Margin
            """, "--discard-on-exit");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Sales/Margin", run.Stdout);
        Assert.DoesNotContain("Not saved yet", run.Stderr);
        Assert.Contains("Discarded unsaved changes.", run.Stderr);
        Assert.Equal(before, SalesTable(model.Path));
    }

    [Theory]
    [InlineData(new string[0], 1)]
    [InlineData(new[] { "--discard-on-exit" }, 0)]
    [InlineData(new[] { "--yes" }, 0)]
    public void EndOfInput_WithUnsavedChanges_FailsUnlessToldToDiscard(string[] flags, int exitCode)
    {
        using var model = SampleModel.CopyToTemp();
        var before = SalesTable(model.Path);

        var run = Run(model.Path, "add Sales/Margin -t Measure -e \"1\"", flags);

        Assert.Equal(exitCode, run.ExitCode);
        Assert.Equal(exitCode != 0, run.Stderr.Contains("'exit' would discard them", StringComparison.Ordinal));
        Assert.Equal(before, SalesTable(model.Path));
    }

    [Fact]
    public void Exit_WithUnsavedChanges_FailsAndRunsNothingAfter()
    {
        using var model = SampleModel.CopyToTemp();

        var run = Run(model.Path, """
            add Sales/Margin -t Measure -e "1"
            exit
            add Sales/Other -t Measure -e "2"
            """);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("'exit' would discard them", run.Stderr);
        Assert.DoesNotContain("Sales/Other", run.Stdout);
    }

    [Fact]
    public void Exit_WhenSaved_LeavesWithoutAsking()
    {
        using var model = SampleModel.CopyToTemp();

        var run = Run(model.Path, """
            add Sales/Margin -t Measure -e "1"
            save
            exit
            add Sales/Other -t Measure -e "2"
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.DoesNotContain("Sales/Other", run.Stdout);
        Assert.Contains("measure Margin = 1", SalesTable(model.Path));
    }

    [Fact]
    public void Batch_StopsAtTheFirstFailure()
    {
        using var model = SampleModel.CopyToTemp();

        var run = Run(model.Path, """
            set Sales/NoSuchMeasure -p description=x
            add Sales/Margin -t Measure -e "1"
            """);

        Assert.NotEqual(0, run.ExitCode);
        Assert.DoesNotContain("Added", run.Stdout);
        Assert.Contains("Stopped at line 1", run.Stderr);
    }

    [Fact]
    public void NoBatch_RunsPastFailures_AndExitsWithTheFirstFailure()
    {
        using var model = SampleModel.CopyToTemp();

        var run = Run(model.Path, """
            set Sales/NoSuchMeasure -p description=x
            add Sales/Margin -t Measure -e "1"
            save
            """, "--no-batch");

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("Added: Sales/Margin", run.Stdout);
        Assert.Contains("measure Margin = 1", SalesTable(model.Path));
    }

    [Fact]
    public void Transaction_CommitsAsOneUndoStep()
    {
        using var model = SampleModel.CopyToTemp();

        var run = Run(model.Path, """
            begin margins
            add Sales/A -t Measure -e "1"
            add Sales/B -t Measure -e "2"
            commit
            history --output-format json
            undo
            status --output-format json
            """, "--discard-on-exit");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Committed: margins (2 changes)", run.Stdout);
        Assert.Contains("Undone: margins (2 changes)", run.Stdout);
        var json = JsonDocuments(run.Stdout);
        var steps = CommandJson.Data(json[0])["steps"]!.AsArray();
        var step = Assert.Single(steps);
        Assert.Equal("margins", (string?)step!["label"]);
        Assert.Equal(2, (int)step["changes"]!);
        var status = CommandJson.Data(json[1]);
        Assert.Equal(0, (int)status["undoSteps"]!);
        Assert.Equal(1, (int)status["redoSteps"]!);
        Assert.False((bool)status["dirty"]!);
    }

    [Fact]
    public void Transaction_RollbackDiscardsItsChanges()
    {
        using var model = SampleModel.CopyToTemp();

        var run = Run(model.Path, """
            begin
            add Sales/A -t Measure -e "1"
            rollback
            status --output-format json
            get Sales/A
            """, "--no-batch");

        var status = CommandJson.Data(JsonDocuments(run.Stdout)[0]);
        Assert.False((bool)status["dirty"]!);
        Assert.Null(status["transaction"]);
        Assert.Contains("Rolled back:", run.Stdout);
        Assert.DoesNotContain("Discarded", run.Stderr);
    }

    [Fact]
    public void Transaction_LeftOpenAtTheEnd_IsRolledBack()
    {
        using var model = SampleModel.CopyToTemp();
        var before = SalesTable(model.Path);

        var strict = Run(model.Path, "begin\nadd Sales/A -t Measure -e \"1\"");
        var discard = Run(model.Path, "begin\nadd Sales/A -t Measure -e \"1\"", "--discard-on-exit");

        Assert.Equal(1, strict.ExitCode);
        Assert.Contains("an open transaction", strict.Stderr);
        Assert.Equal(0, discard.ExitCode);
        Assert.Contains("Discarded an open transaction.", discard.Stderr);
        Assert.Equal(before, SalesTable(model.Path));
    }

    [Theory]
    [InlineData("undo", "TOMIX_SESSION_NOTHING_TO_UNDO", 1)]
    [InlineData("redo", "TOMIX_SESSION_NOTHING_TO_REDO", 1)]
    [InlineData("commit", "TOMIX_SESSION_NO_TRANSACTION", 2)]
    [InlineData("rollback", "TOMIX_SESSION_NO_TRANSACTION", 2)]
    [InlineData("begin\nbegin", "TOMIX_SESSION_TRANSACTION_OPEN", 2)]
    [InlineData("begin\nundo", "TOMIX_SESSION_IN_TRANSACTION", 2)]
    [InlineData("deploy", "TOMIX_SESSION_COMMAND_UNAVAILABLE", 2)]
    [InlineData("set 'Sales'[Total Sales] -p description=x --stage", "TOMIX_SESSION_STAGE_UNSUPPORTED", 2)]
    public void SessionErrors_HaveCodes(string script, string code, int exitCode)
    {
        using var model = SampleModel.CopyToTemp();

        var run = Run(model.Path, script, "--discard-on-exit", "--error-format", "json");

        Assert.Equal(exitCode, run.ExitCode);
        Assert.Contains($"\"{code}\"", run.Stderr);
    }

    [Fact]
    public void ACommandNamingAnotherModel_IsRejected()
    {
        using var model = SampleModel.CopyToTemp();
        using var other = SampleModel.CopyToTemp();

        var run = Run(model.Path, $"get Sales -m \"{other.Path}\"", "--error-format", "json");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("\"TOMIX_SESSION_MODEL_MISMATCH\"", run.Stderr);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("ls")]
    [InlineData("undo")]
    public void WithNoModelOpen_ModelCommandsSaySo(string line)
    {
        var run = RunAs("interactive", "", line, TestServices.Create(), "--error-format", "json");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("\"TOMIX_SESSION_NO_MODEL\"", run.Stderr);
    }

    [Fact]
    public void Connect_OpensTheModel_ThatCommandsThenRunOn()
    {
        using var model = SampleModel.CopyToTemp();

        var run = RunAs("interactive", "", $"""
            connect "{model.Path}"
            add Sales/Margin -t Measure -e "1"
            save
            """, TestServices.Create());

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("tables: 3", run.Stdout);
        Assert.Contains($"The session now edits {Path.GetFileName(model.Path)}.", run.Stderr);
        Assert.Contains("measure Margin = 1", SalesTable(model.Path));
    }

    [Fact]
    public void Connect_WithUnsavedChanges_KeepsTheOpenModelUnlessToldToDiscard()
    {
        using var first = SampleModel.CopyToTemp();
        using var second = SampleModel.CopyToTemp();
        var script = $"""
            add Sales/Margin -t Measure -e "1"
            connect "{second.Path}"
            status --output-format json
            """;

        var strict = Run(first.Path, script, "--no-batch");
        var discard = Run(first.Path, script, "--discard-on-exit");

        Assert.Contains("'connect to another model' would discard them", strict.Stderr);
        Assert.Equal(first.Path, (string?)CommandJson.Data(JsonDocuments(strict.Stdout)[^1])["source"]);
        Assert.Equal(0, discard.ExitCode);
        Assert.Equal(second.Path, (string?)CommandJson.Data(JsonDocuments(discard.Stdout)[^1])["source"]);
        Assert.False((bool)CommandJson.Data(JsonDocuments(discard.Stdout)[^1])["dirty"]!);
    }

    [Fact]
    public void Connect_ToAModelThatFailsToLoad_KeepsTheOpenOneAndItsChanges()
    {
        using var model = SampleModel.CopyToTemp();

        var run = Run(model.Path, """
            add Sales/Margin -t Measure -e "1"
            connect ./no-such-model
            status --output-format json
            """, "--no-batch", "--discard-on-exit");

        var status = CommandJson.Data(JsonDocuments(run.Stdout)[^1]);
        Assert.Equal(model.Path, (string?)status["source"]);
        Assert.True((bool)status["dirty"]!);
        Assert.Contains("No provider can open model", run.Stderr);
    }

    [Fact]
    public void Autosave_SavesAfterEveryChange()
    {
        using var model = SampleModel.CopyToTemp();

        var run = Run(model.Path, "add Sales/Margin -t Measure -e \"1\"", "--autosave");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("measure Margin = 1", SalesTable(model.Path));
    }

    [Fact]
    public void OutputFormatJson_IsEveryLinesDefault()
    {
        using var model = SampleModel.CopyToTemp();

        var run = Run(model.Path, "status", "--output-format", "json");

        Assert.Equal(0, run.ExitCode);
        var status = CommandJson.Data(run.Stdout);
        Assert.Equal(model.Path, (string?)status["source"]);
        Assert.False((bool)status["dirty"]!);
    }

    [Fact]
    public void CommentsAndBlankLinesAreSkipped_AndEchoShowsEachLine()
    {
        using var model = SampleModel.CopyToTemp();

        var run = Run(model.Path, "# a comment\n\n   \nstatus", "--echo");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("> status", run.Stderr);
        Assert.DoesNotContain("> # a comment", run.Stderr);
        Assert.Contains("Unsaved changes: no", run.Stdout);
    }

    [Fact]
    public void ShellIsAnAlias_AndBimFilesOpen()
    {
        using var dir = new TempDir();
        var bim = SampleModel.CopyFileTo(dir, "model.bim", "basic-tmdl.bim");

        var run = RunAs("shell", bim, """
            add Sales/Margin -t Measure -e "1"
            save
            """, TestServices.Create());

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Margin", File.ReadAllText(bim));
    }

    [Fact]
    public void StagedWork_BlocksOpeningASession()
    {
        using var model = SampleModel.CopyToTemp();
        var services = TestServices.Create();
        var stage = ConsoleCapture.InvokeThroughProgram(
            Root(services).Parse(["set", "Sales/Total Sales", "-p", "description=x", "--stage", "-m", model.Path]));
        Assert.Equal(0, stage.ExitCode);

        var run = RunAs("interactive", model.Path, "status", services);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("has staged changes", run.Stderr);
    }

    [Theory]
    [InlineData("C:/models/Sales Model", "Sales Model")]
    [InlineData("C:/models/Sales Model/", "Sales Model")]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/Finance", "Revenue")]
    public void ModelName_IsTheFolderOrDatabase(string value, string expected)
        => Assert.Equal(expected, Interactive.InteractiveLoop.ModelName(new ModelReference(value, expected == "Revenue" ? "Revenue" : null)));

    [Theory]
    [InlineData("localhost:50623", "Power BI Desktop is not running; 'save -o <folder>' writes the changes to files")]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/Finance", "cannot be reached; 'save -o <folder>' writes the changes to files")]
    public void Status_SaysWhenTheServerIsUnreachable(string model, string expected)
        => Assert.Equal(expected, Interactive.SessionCommands.Unreachable(model));

    private static ConsoleCapture.Captured Run(string modelPath, string script, params string[] flags)
        => RunAs("interactive", modelPath, script, TestServices.Create(), flags);

    private static ConsoleCapture.Captured RunAs(
        string command, string modelPath, string script, AppServices services, params string[] flags)
    {
        var root = Root(services);
        InputValueResolver.Stdin.Value = new StringReader(script);
        try
        {
            string[] args = modelPath.Length == 0 ? [command, .. flags] : [command, modelPath, .. flags];
            var captured = ConsoleCapture.InvokeThroughProgram(root.Parse(args), captureAnsiConsole: true);
            return captured with { Stdout = StripAnsi(captured.Stdout), Stderr = StripAnsi(captured.Stderr) };
        }
        finally
        {
            InputValueResolver.Stdin.Value = null;
        }
    }

    private static System.CommandLine.RootCommand Root(AppServices services)
        => Program.BuildRootCommand(Providers, new CompositeExpressionFormatterClient([]), TestRoot.Version, services);

    private static string SalesTable(string modelPath)
        => File.ReadAllText(Path.Combine(modelPath, "tables", "Sales.tmdl"));

    /// <summary>The JSON documents in <paramref name="stdout"/>, which holds one per JSON line.</summary>
    private static List<string> JsonDocuments(string stdout)
    {
        var documents = new List<string>();
        var start = -1;
        var depth = 0;
        for (var i = 0; i < stdout.Length; i++)
        {
            if (stdout[i] == '{' && depth++ == 0)
                start = i;
            else if (stdout[i] == '}' && --depth == 0)
                documents.Add(stdout[start..(i + 1)]);
        }

        return documents;
    }

    private static string StripAnsi(string text) => AnsiRegex().Replace(text, "");

    [GeneratedRegex(@"\x1b\[[0-9;]*m")]
    private static partial Regex AnsiRegex();
}
