using System.CommandLine;
using System.Text.Json;
using Tomix.App.State;
using Tomix.Cli.Commands;

namespace Tomix.Cli.Tests;

[Collection(ConsoleStateCollection.Name)]
public sealed class DeployProfileTests
{
    [Fact]
    public void MissingProfile_ReportsProfileError_BeforeConfirmation()
    {
        var services = TestServices.Create();
        var root = TestRoot.With(new DeployCommand([], services.State).Build());

        var captured = ConsoleCapture.InvokeThroughProgram(root.Parse([
            "deploy", "model.bim", "--profile", "sandbox", "--non-interactive"
        ]));

        Assert.Equal(1, captured.ExitCode);
        Assert.Contains("Profile 'sandbox' not found", captured.Stderr);
        Assert.Contains("tx profile list", captured.Stderr);
        Assert.DoesNotContain("needs confirmation", captured.Stderr);
    }

    [Fact]
    public void MissingProfile_UsesJsonErrorEnvelope_BeforeConfirmation()
    {
        var services = TestServices.Create();
        var root = TestRoot.With(new DeployCommand([], services.State).Build());

        var captured = ConsoleCapture.InvokeThroughProgram(root.Parse([
            "deploy", "model.bim", "--profile", "sandbox", "--non-interactive",
            "--output-format", "json"
        ]));

        Assert.Equal(1, captured.ExitCode);
        Assert.Empty(captured.Stdout);
        using var document = JsonDocument.Parse(captured.Stderr);
        Assert.Equal("TOMIX_PROFILE_NOT_FOUND", document.RootElement.GetProperty("code").GetString());
        Assert.Contains("sandbox", document.RootElement.GetProperty("error").GetString());
        Assert.Contains("tx profile set", document.RootElement.GetProperty("hint").GetString());
    }

    [Fact]
    public void ServerlessProfile_ReportsProfileError_BeforeConfirmation()
    {
        var services = TestServices.Create();
        services.State.SaveProfiles(new Dictionary<string, CliProfile>
        {
            ["local"] = new("local", null, null, "./model", null, null, Local: true)
        });
        var root = TestRoot.With(new DeployCommand([], services.State).Build());

        var captured = ConsoleCapture.InvokeThroughProgram(root.Parse([
            "deploy", "model.bim", "--profile", "local", "--non-interactive",
            "--error-format", "json"
        ]));

        Assert.Equal(2, captured.ExitCode);
        using var document = JsonDocument.Parse(captured.Stderr);
        Assert.Equal("TOMIX_DEPLOY_PROFILE_NO_SERVER", document.RootElement.GetProperty("code").GetString());
        Assert.Contains("local", document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void ProfileHelp_ShowsCreationAndDeployExamples()
    {
        var root = TestRoot.Full();

        var deployHelp = ConsoleCapture.Invoke(root.Parse(["deploy", "--help"]), captureAnsiConsole: true);
        var setHelp = ConsoleCapture.Invoke(root.Parse(["profile", "set", "--help"]), captureAnsiConsole: true);

        Assert.Equal(0, deployHelp.ExitCode);
        Assert.Contains("tx profile list", deployHelp.Stdout);
        Assert.Contains("tx profile set", deployHelp.Stdout);
        Assert.Contains("tx deploy ./model.tmdl --profile prod --dry-run", deployHelp.Stdout);
        Assert.Equal(0, setHelp.ExitCode);
        Assert.Contains("tx profile set dev -s MyWorkspace -d Sales", setHelp.Stdout);
        Assert.Contains("tx profile set dev --from-active", setHelp.Stdout);
    }
}
