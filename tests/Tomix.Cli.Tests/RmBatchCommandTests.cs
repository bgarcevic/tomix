using System.CommandLine;
using System.Text.Json;
using Tomix.Cli.Commands;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;

namespace Tomix.Cli.Tests;

/// <summary>
/// Batch <c>tx rm</c>: several path arguments or <c>--from</c> (a file or stdin, plain paths or JSON
/// Lines) remove objects as one change. A trailing model path still selects the model, a single
/// path keeps the single-object result, and a blocked preview names the flags that would remove it.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed partial class RmBatchCommandTests
{
    private static readonly IReadOnlyList<IModelProvider> Providers = [new TmdlModelProvider()];

    private static RootCommand BuildRoot()
    {
        var services = TestServices.Create();
        return TestRoot.With(new RmCommand(Providers, services.State, services.Mutations).Build());
    }

    [Fact]
    public void Several_paths_preview_together()
    {
        using var model = SampleModel.CopyToTemp();
        var before = Files(model.Path);

        var captured = ConsoleCapture.Invoke(
            BuildRoot().Parse(["rm", "Sales/Order Count", "Sales/Distinct Customers", "-m", model.Path]),
            captureAnsiConsole: true);

        Assert.Equal(0, captured.ExitCode);
        var output = StripAnsi(captured.Stdout);
        Assert.Contains("Would remove 2 object(s):", output);
        Assert.Contains("Sales/Order Count (measure)", output);
        Assert.Contains("Sales/Distinct Customers (measure)", output);
        Assert.Contains("Not saved yet.", StripAnsi(captured.Stderr));
        Assert.Equal(before, Files(model.Path));
    }

    [Fact]
    public void A_trailing_model_path_selects_the_model()
    {
        using var model = SampleModel.CopyToTemp();

        var data = Json(["rm", "Sales/Order Count", "Sales/Distinct Customers", model.Path]);

        Assert.Equal(["Sales/Order Count", "Sales/Distinct Customers"], Strings(data.GetProperty("wouldRemove")));
    }

    [Fact]
    public void One_path_and_a_model_keeps_the_single_object_result()
    {
        using var model = SampleModel.CopyToTemp();

        var data = Json(["rm", "Sales/Order Count", model.Path]);

        Assert.Equal("Sales/Order Count", data.GetProperty("wouldRemove").GetString());
        Assert.False(data.TryGetProperty("targets", out _));
    }

    [Fact]
    public void From_a_file_of_json_lines_and_paths_saves_one_change()
    {
        using var model = SampleModel.CopyToTemp();
        using var dir = new TempDir();
        var list = dir.WriteFile("unused.jsonl", string.Join("\n",
            """{"type":"Measure","table":"Sales","name":"Order Count","reason":"unused","references":0}""",
            "# a comment",
            "Sales/Distinct Customers",
            """{"type":"column","path":"Products/Category"}"""));

        var data = Json(["rm", "--from", list, "-m", model.Path, "--save", "--yes"]);

        Assert.Equal("saved", data.GetProperty("status").GetString());
        Assert.Equal(
            ["Sales/Order Count", "Sales/Distinct Customers", "Products/Category"],
            Strings(data.GetProperty("removed")));
        Assert.Equal(["measure", "measure", "column"],
            data.GetProperty("targets").EnumerateArray().Select(t => t.GetProperty("type").GetString()));
        var sales = File.ReadAllText(Path.Combine(model.Path, "tables", "Sales.tmdl"));
        Assert.DoesNotContain("Order Count", sales);
        Assert.DoesNotContain("Distinct Customers", sales);
        Assert.DoesNotContain("column Category", File.ReadAllText(Path.Combine(model.Path, "tables", "Products.tmdl")));
    }

    [Fact]
    public void From_stdin()
    {
        using var model = SampleModel.CopyToTemp();

        InputValueResolver.Stdin.Value = new StringReader("Sales/Order Count\n{\"type\":\"measure\",\"table\":\"Sales\",\"name\":\"Avg Sale\"}\n");
        JsonElement data;
        try
        {
            data = Json(["rm", "--from", "-", "-m", model.Path]);
        }
        finally
        {
            InputValueResolver.Stdin.Value = null;
        }

        Assert.Equal(["Sales/Order Count", "Sales/Avg Sale"], Strings(data.GetProperty("wouldRemove")));
    }

    [Theory]
    [InlineData("""{"type":"measure","name":"Order Count"}""", "missing \"table\"")]
    [InlineData("""{"type":"gizmo","name":"X"}""", "unknown type 'gizmo'")]
    [InlineData("", "No objects to remove.")]
    public void Bad_from_input_exits_2_before_opening_the_model(string content, string message)
    {
        using var dir = new TempDir();
        var list = dir.WriteFile("list.jsonl", content);

        var captured = ConsoleCapture.Invoke(
            BuildRoot().Parse(["rm", "--from", list, "-m", dir.Combine("no-such-model"), "--error-format", "json"]),
            captureAnsiConsole: true);

        Assert.Equal(2, captured.ExitCode);
        Assert.Contains("TOMIX_RM_INPUT_INVALID", captured.Stderr);
        using var error = JsonDocument.Parse(captured.Stderr);
        Assert.Contains(message, error.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void A_blocked_preview_hints_the_flags_that_remove_it()
    {
        // Distinct Customers uses Sales[CustomerID] (DAX), and a relationship uses it (cascade).
        using var model = SampleModel.CopyToTemp();

        var captured = ConsoleCapture.Invoke(
            BuildRoot().Parse(["rm", "Sales/CustomerID", "Sales/Order Count", "-m", model.Path]),
            captureAnsiConsole: true);

        Assert.Equal(0, captured.ExitCode);
        // The console wraps long lines; compare without line breaks.
        var output = Unwrapped(captured.Stdout);
        Assert.Contains("Would also change:", output);
        Assert.Contains("relationship 'Sales'[CustomerID] -> 'Customers'[CustomerID] (from Sales/CustomerID)", output);
        Assert.Contains("Would break 1 DAX reference(s) in: Sales/Distinct Customers.", output);
        Assert.Contains(" --force --cascade --save", Unwrapped(captured.Stderr));
    }

    [Fact]
    public void Saving_a_batch_that_cascades_needs_cascade()
    {
        using var model = SampleModel.CopyToTemp();
        var before = Files(model.Path);

        var captured = ConsoleCapture.Invoke(
            BuildRoot().Parse(["rm", "Sales/ProductID", "Sales/Order Count", "-m", model.Path, "--save", "--yes", "--error-format", "json"]),
            captureAnsiConsole: true);

        Assert.Equal(1, captured.ExitCode);
        Assert.Contains("TOMIX_RM_NEEDS_CASCADE", captured.Stderr);
        Assert.Equal(before, Files(model.Path));

        var data = Json(["rm", "Sales/ProductID", "Sales/Order Count", "-m", model.Path, "--save", "--yes", "--cascade"]);
        Assert.Equal("saved", data.GetProperty("status").GetString());
        var cascade = Assert.Single(data.GetProperty("cascades").EnumerateArray());
        Assert.Equal("relationship", cascade.GetProperty("type").GetString());
        Assert.Equal("Sales/ProductID", cascade.GetProperty("target").GetString());
    }

    [Theory]
    [InlineData(new[] { "rm", "A", "B" }, new[] { "--cascade" }, "tx rm A B --cascade --save")]
    [InlineData(new[] { "rm", "A", "B", "-f" }, new[] { "--force", "--cascade" }, "tx rm A B --force --cascade --save")]
    [InlineData(new[] { "rm", "--from", "x.jsonl", "--cascade" }, new[] { "--force", "--cascade" }, "tx rm --from x.jsonl --force --cascade --save")]
    public void SaveHint_appends_each_flag_once(string[] tokens, string[] flags, string expected)
        => Assert.Equal(expected, RmCommand.SaveHint(tokens, flags));

    [Fact]
    public void Only_an_existing_trailing_path_is_the_model()
    {
        using var dir = new TempDir();

        Assert.Equal("A|B ->", Split(["A", "B"], modelOptionGiven: false));
        Assert.Equal($"A -> {dir.Path}", Split(["A", dir.Path], modelOptionGiven: false));
        Assert.Equal($"A|{dir.Path} ->", Split(["A", dir.Path], modelOptionGiven: true));
        Assert.Equal($"{dir.Path} ->", Split([dir.Path], modelOptionGiven: false));

        static string Split(string[] values, bool modelOptionGiven)
        {
            var (paths, model) = RmCommand.SplitModelArgument(values, modelOptionGiven);
            return $"{string.Join("|", paths)} ->{(model is null ? "" : " " + model)}";
        }
    }

    private static JsonElement Json(string[] args)
    {
        var captured = ConsoleCapture.Invoke(
            BuildRoot().Parse([.. args, "--output-format", "json"]), captureAnsiConsole: true);
        Assert.True(captured.ExitCode == 0, captured.Stdout + captured.Stderr);
        using var document = JsonDocument.Parse(captured.Stdout);
        return document.RootElement.GetProperty("data").Clone();
    }

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToArray();

    [System.Text.RegularExpressions.GeneratedRegex("\x1b\\[[0-9;]*m")]
    private static partial System.Text.RegularExpressions.Regex AnsiRegex();

    private static string StripAnsi(string text) => AnsiRegex().Replace(text, "");

    private static string Unwrapped(string text) => StripAnsi(text).Replace("\r", "").Replace("\n", "");

    private static Dictionary<string, string> Files(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f), File.ReadAllText);
}
