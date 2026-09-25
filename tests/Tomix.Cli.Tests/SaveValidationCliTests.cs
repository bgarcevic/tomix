using System.Text.Json;
using Tomix.Cli.Commands;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;

namespace Tomix.Cli.Tests;

[Collection(ConsoleStateCollection.Name)]
public sealed class SaveValidationCliTests
{
    [Fact]
    public void AddSave_BlockedJsonAndForcedResult_HaveMachineReadableDelta()
    {
        using var model = SampleModel.CopyToTemp();
        var services = TestServices.Create();
        var root = TestRoot.With(new AddCommand(
            [new TmdlModelProvider()], services.State, services.Mutations).Build());
        string[] args =
        [
            "add", "Sales/Broken", model.Path,
            "-t", "Measure", "-i", "SUM('Missing'[Amount])",
            "--save", "--output-format", "json"
        ];

        var blocked = ConsoleCapture.Invoke(root.Parse(args), captureAnsiConsole: true);

        Assert.Equal(1, blocked.ExitCode);
        Assert.Equal("", blocked.Stdout);
        using var error = JsonDocument.Parse(blocked.Stderr);
        Assert.Equal("TOMIX_SAVE_VALIDATION_BLOCKED", error.RootElement.GetProperty("code").GetString());
        Assert.True(error.RootElement.GetProperty("blocked").GetBoolean());
        Assert.Equal("validation", error.RootElement.GetProperty("reason").GetString());
        Assert.Equal("Sales/Broken", error.RootElement.GetProperty("newErrors")[0].GetProperty("object").GetString());

        var forced = ConsoleCapture.Invoke(root.Parse([.. args, "--force"]), captureAnsiConsole: true);

        Assert.Equal(0, forced.ExitCode);
        using var output = JsonDocument.Parse(forced.Stdout);
        Assert.Equal(1, output.RootElement.GetProperty("data").GetProperty("newValidationErrors").GetInt32());
        Assert.Equal("TOMIX_SAVE_VALIDATION_FORCED",
            output.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
    }
}
