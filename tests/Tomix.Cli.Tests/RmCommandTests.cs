using System.CommandLine;
using System.Text.Json;
using Tomix.Cli.Commands;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;

namespace Tomix.Cli.Tests;

/// <summary>
/// The user-facing half of the rm <c>--dry-run</c> fix (issue #217): a preview renders
/// "Would remove:" and exits 0 — even when the reference guard would block, where it lists
/// the dependents and hints <c>--force</c> instead of failing — and never persists anything.
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
    public void DryRun_UnreferencedObject_RendersWouldRemove_ExitsZero()
    {
        using var model = SampleModel.CopyToTemp();
        var before = Snapshot(model.Path);

        var captured = ConsoleCapture.Invoke(
            BuildRoot().Parse(["rm", "Sales/Total Sales", "-m", model.Path, "--dry-run"]),
            captureAnsiConsole: true);

        Assert.Equal(0, captured.ExitCode);
        var output = StripAnsi(captured.Stdout);
        Assert.Contains("Would remove:", output);
        Assert.Contains("Sales/Total Sales", output);
        Assert.Contains("Dry run: nothing was saved.", output);
        Assert.DoesNotContain("Removed:", output);
        Assert.Equal(before, Snapshot(model.Path));
    }

    [Fact]
    public void DryRun_GuardedObject_ReportsDependentsAndHintsForce_ExitsZero()
    {
        // Sales/Amount is referenced by the table's own measures (Total Sales, Avg Sale), so the
        // guard would block a real removal — the preview must say so, not exit 1.
        using var model = SampleModel.CopyToTemp();
        var before = Snapshot(model.Path);

        var captured = ConsoleCapture.Invoke(
            BuildRoot().Parse(["rm", "Sales/Amount", "-m", model.Path, "--dry-run"]),
            captureAnsiConsole: true);

        Assert.Equal(0, captured.ExitCode);
        var output = StripAnsi(captured.Stdout);
        Assert.Contains("Would remove:", output);
        Assert.Contains("Would break 2 DAX reference(s)", output);
        Assert.Contains("Sales/Total Sales", output);
        Assert.Contains("Re-run with --force", output);
        Assert.DoesNotContain("Removed:", output);
        Assert.Equal(before, Snapshot(model.Path));
    }

    [Fact]
    public void DryRun_GuardedObject_JsonCarriesPreviewContract()
    {
        using var model = SampleModel.CopyToTemp();

        var captured = ConsoleCapture.Invoke(
            BuildRoot().Parse(["rm", "Sales/Amount", "-m", model.Path, "--dry-run", "--output-format", "json"]),
            captureAnsiConsole: true);

        Assert.Equal(0, captured.ExitCode);
        using var document = JsonDocument.Parse(captured.Stdout);
        var data = document.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("dryRun").GetBoolean());
        Assert.Equal("would_block", data.GetProperty("reason").GetString());
        Assert.Equal("dryRun", data.GetProperty("status").GetString());
        Assert.Equal("Sales/Amount", data.GetProperty("wouldRemove").GetString());
        Assert.False(data.TryGetProperty("removed", out _));
        Assert.False(data.GetProperty("saved").GetBoolean());
    }

    [System.Text.RegularExpressions.GeneratedRegex("\x1b\\[[0-9;]*m")]
    private static partial System.Text.RegularExpressions.Regex AnsiRegex();

    private static string StripAnsi(string text) => AnsiRegex().Replace(text, "");

    private static Dictionary<string, string> Snapshot(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f), File.ReadAllText);
}
