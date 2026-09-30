using System.CommandLine;
using System.Text.Json;
using Tomix.Cli.Commands;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;

namespace Tomix.Cli.Tests;

/// <summary>
/// The user-facing half of the rm preview fix (issue #217): rm without --save/--stage renders
/// "Would remove:" and exits 0 — even when the reference guard would block, where it lists
/// the dependents and the <c>--force --save</c> command instead of failing — and never persists anything.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed partial class RmCommandTests
{
    private static readonly IReadOnlyList<IModelProvider> Providers = [new TmdlModelProvider()];

    private static RootCommand BuildRoot()
    {
        var services = TestServices.Create();
        return TestRoot.With(new RmCommand(Providers, services.State, services.Mutations).Build());
    }

    [Fact]
    public void Preview_UnreferencedObject_RendersWouldRemove_ExitsZero()
    {
        using var model = SampleModel.CopyToTemp();
        var before = Snapshot(model.Path);

        var captured = ConsoleCapture.Invoke(
            BuildRoot().Parse(["rm", "Sales/Total Sales", "-m", model.Path]),
            captureAnsiConsole: true);

        Assert.Equal(0, captured.ExitCode);
        var output = StripAnsi(captured.Stdout);
        Assert.Contains("Would remove:", output);
        Assert.Contains("Sales/Total Sales", output);
        // Hints are commentary (#255): stderr, so the result on stdout stays pipeable.
        Assert.Contains("Not saved yet.", StripAnsi(captured.Stderr));
        Assert.DoesNotContain("Not saved", output);
        Assert.DoesNotContain("Removed:", output);
        Assert.Equal(before, Snapshot(model.Path));
    }

    [Fact]
    public void Preview_GuardedObject_ReportsDependentsAndHintsForce_ExitsZero()
    {
        // Sales/Amount is referenced by the table's own measures (Total Sales, Avg Sale), so the
        // guard would block a real removal — the preview must say so, not exit 1.
        using var model = SampleModel.CopyToTemp();
        var before = Snapshot(model.Path);

        var captured = ConsoleCapture.Invoke(
            BuildRoot().Parse(["rm", "Sales/Amount", "-m", model.Path]),
            captureAnsiConsole: true);

        Assert.Equal(0, captured.ExitCode);
        var output = StripAnsi(captured.Stdout);
        Assert.Contains("Would remove:", output);
        Assert.Contains("Would break 2 DAX reference(s)", output);
        Assert.Contains("Sales/Total Sales", output);
        // One copy-pasteable next step, not "--force" and "--save" as separate half-steps.
        var stderr = StripAnsi(captured.Stderr);
        Assert.Contains("To remove anyway: tx rm Sales/Amount -m ", stderr);
        Assert.Contains(" --force --save", stderr);
        Assert.DoesNotContain("Not saved yet", stderr);
        Assert.DoesNotContain("Removed:", output);
        Assert.Equal(before, Snapshot(model.Path));
    }

    [Fact]
    public void Preview_GuardedObject_JsonCarriesPreviewContract()
    {
        using var model = SampleModel.CopyToTemp();

        var captured = ConsoleCapture.Invoke(
            BuildRoot().Parse(["rm", "Sales/Amount", "-m", model.Path, "--output-format", "json"]),
            captureAnsiConsole: true);

        Assert.Equal(0, captured.ExitCode);
        using var document = JsonDocument.Parse(captured.Stdout);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal("would_block", data.GetProperty("reason").GetString());
        Assert.Equal("preview", data.GetProperty("status").GetString());
        Assert.Equal("Sales/Amount", data.GetProperty("wouldRemove").GetString());
        Assert.False(data.TryGetProperty("removed", out _));
        Assert.False(data.GetProperty("saved").GetBoolean());
    }

    [Theory]
    [InlineData(new[] { "rm", "Sales/Amount" }, "tx rm Sales/Amount --force --save")]
    [InlineData(new[] { "rm", "Sales/Total Sales", "-m", "m.bim", "-f" }, "tx rm 'Sales/Total Sales' -m m.bim --force --save")]
    public void ForceSaveHint_EchoesTheCommandLine(string[] tokens, string expected)
    {
        // Quoting is platform-specific only for embedded apostrophes, which these tokens lack.
        Assert.Equal(expected, RmCommand.ForceSaveHint(tokens));
    }

    [System.Text.RegularExpressions.GeneratedRegex("\x1b\\[[0-9;]*m")]
    private static partial System.Text.RegularExpressions.Regex AnsiRegex();

    private static string StripAnsi(string text) => AnsiRegex().Replace(text, "");

    private static Dictionary<string, string> Snapshot(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f), File.ReadAllText);
}
