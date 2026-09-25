using System.CommandLine;
using System.Text.Json.Nodes;
using Tomix.Cli.Commands;
using Tomix.Provider.Tmdl;

namespace Tomix.Cli.Tests;

[Collection(ConsoleStateCollection.Name)]
public sealed class RefreshPolicyCommandTests
{
    [Fact]
    public void OldCommandIsRemoved()
        => Assert.DoesNotContain(TestRoot.Full().Subcommands, c => c.Name == "incremental-refresh");

    [Theory]
    [InlineData("-p")]
    [InlineData("--set")]
    public void Set_CreatesPolicyFromRepeatedAssignments(string option)
    {
        using var model = SampleModel.CopyToTemp();
        var services = TestServices.Create();
        var root = TestRoot.With(new SetCommand([new TmdlModelProvider()], services.State, services.Mutations).Build());
        var parsed = root.Parse(["set", "Sales/RefreshPolicy", "--model", model.Path,
            option, "RollingWindowGranularity=year", option, "RollingWindowPeriods=10",
            option, "IncrementalGranularity=day", option, "IncrementalPeriods=3",
            option, "SourceExpression=let Source = RangeStart, End = RangeEnd in Source",
            "--output-format", "json", "--save", "--no-sync"]);
        Assert.Empty(parsed.Errors);
        var captured = ConsoleCapture.Invoke(parsed);
        Assert.Equal(0, captured.ExitCode);
        Assert.Contains("incrementalPeriods", captured.Stdout);
        Assert.Contains("RangeStart", captured.Stdout);

        root.Subcommands.Add(new GetCommand([new TmdlModelProvider()], services.State).Build());
        root.Subcommands.Add(new LsCommand([new TmdlModelProvider()], services.State).Build());
        var getResult = ConsoleCapture.Invoke(root.Parse(["get", "Sales/RefreshPolicy", "--model", model.Path, "--output-format", "json"]));
        var lsResult = ConsoleCapture.Invoke(root.Parse(["ls", "Sales/RefreshPolicy", "--model", model.Path, "--output-format", "json"]));
        Assert.Equal(0, getResult.ExitCode);
        Assert.Equal(0, lsResult.ExitCode);
        var get = CommandJson.Data(getResult.Stdout);
        var row = Assert.Single(CommandJson.DataArray(lsResult.Stdout))!.AsObject();
        row.Remove("path");
        row.Remove("type");
        Assert.True(JsonNode.DeepEquals(get["properties"], row));
        Assert.Equal(3, get["properties"]!["incrementalPeriods"]!.GetValue<int>());
        Assert.IsType<JsonArray>(get["properties"]!["issues"]);

        var csv = ConsoleCapture.Invoke(root.Parse(["get", "Sales/RefreshPolicy", "--model", model.Path, "--output-format", "csv"]));
        Assert.Equal(0, csv.ExitCode);
        Assert.Contains("PolicyPartitions,Issues", csv.Stdout);
        Assert.DoesNotContain("System.Collections", csv.Stdout);
    }

    [Theory]
    [InlineData("--refresh-type", "automatic")]
    [InlineData("--apply-refresh-policy", "false")]
    [InlineData("--trace", "should-not-exist.log")]
    public void PolicyOnly_RejectsConflictsBeforeConfirmation(string option, string value)
    {
        var services = TestServices.Create();
        var root = TestRoot.With(new RefreshCommand([], services.State, services.LoadCurrentSession).Build());
        var captured = ConsoleCapture.Invoke(root.Parse(["refresh", "--policy-only", "--table", "Sales", option, value, "--non-interactive", "--error-format", "json"]));
        Assert.Equal(2, captured.ExitCode);
        Assert.Contains("TOMIX_REFRESH_POLICY_OPTIONS_CONFLICT", captured.Stderr);
        Assert.DoesNotContain("Pass --yes", captured.Stderr);
    }
}
