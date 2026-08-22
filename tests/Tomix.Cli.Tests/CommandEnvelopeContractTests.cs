using System.Text.Json.Nodes;
using Tomix.Cli.Commands;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;

namespace Tomix.Cli.Tests;

/// <summary>
/// Pins the stdout JSON envelope end-to-end, and — just as importantly — pins what is deliberately
/// left outside it.
/// </summary>
/// <remarks>
/// The per-command JSON contract tests (<see cref="BpaJsonContractTests"/>,
/// <see cref="QueryResultJsonContractTests"/>, <see cref="TestRunJsonContractTests"/>,
/// <see cref="MutationResultContractTests"/>) all serialize a projector directly, so none of them
/// observes the wrapper the command actually prints. That is the right level for them — they pin
/// the shape of <c>data</c> — but it leaves the envelope itself unguarded, which is how the
/// documented <c>(data, diagnostics)</c> contract managed to not exist in the code at all.
/// <para>
/// Every boundary is covered: the envelope on <c>ls</c>/<c>get</c>, the csv and model-shaped
/// formats, and <c>deploy --xmla</c> below. <c>query --output-file</c> is covered by
/// <see cref="QueryOutputFileContractTests"/>.
/// </para>
/// </remarks>
[Collection(ConsoleStateCollection.Name)]
public sealed class CommandEnvelopeContractTests
{
    private static readonly IReadOnlyList<IModelProvider> Providers = [new TmdlModelProvider()];
    private static readonly string SampleTmdl = SampleModel.Locate();

    [Theory]
    [InlineData("ls")]
    [InlineData("get")]
    public void JsonOutput_IsWrappedInTheDataDiagnosticsEnvelope(string command)
    {
        var stdout = Invoke(command, command == "get" ? "Sales" : "Sale*", SampleTmdl,
            "--output-format", "json");

        var root = JsonNode.Parse(stdout)!.AsObject();

        Assert.Equal(["data", "diagnostics"], root.Select(kvp => kvp.Key).Order());
        Assert.NotNull(root["data"]);
        // Always present, empty until a handler emits a non-fatal diagnostic.
        Assert.Empty(root["diagnostics"]!.AsArray());
    }

    [Theory]
    // CSV is a tabular contract; a wrapper has nowhere to go in it.
    [InlineData("ls", "csv")]
    // Model-shaped formats are fragments of a model, not command results — a consumer feeds them
    // back to a serializer, so an envelope would corrupt them.
    [InlineData("get", "tmdl")]
    [InlineData("get", "bim")]
    [InlineData("get", "tmsl")]
    public void NonJsonFormats_AreNeverEnveloped(string command, string format)
    {
        var stdout = Invoke(command, command == "get" ? "Sales" : "Sale*", SampleTmdl,
            "--output-format", format);

        Assert.DoesNotContain("\"diagnostics\"", stdout);
        if (format is "bim" or "tmsl")
        {
            // Still JSON — but the model's own shape, with no envelope keys above it.
            var root = JsonNode.Parse(stdout)!.AsObject();
            Assert.False(root.ContainsKey("data"));
            Assert.Equal("Sales", root["name"]!.GetValue<string>());
        }
    }

    /// <summary>
    /// <c>deploy --xmla -</c> is the one boundary where the same command emits both shapes: text
    /// mode writes the raw TMSL for the engine, JSON mode is an ordinary command result with the
    /// script inside <c>data</c>. Both directions are asserted, since the interesting regression is
    /// either one adopting the other's shape.
    /// </summary>
    /// <remarks>
    /// No live endpoint is involved: <c>--deploy-full</c> preserves nothing, so
    /// <c>RequiresTargetRead</c> is false and <c>GenerateScriptAsync</c> returns before it would
    /// connect (<c>TomModelDeployer.cs</c>). An earlier revision of this file claimed the branch
    /// needed a real server and left it unpinned on that basis — it does not.
    /// </remarks>
    [Fact]
    public void DeployXmla_IsRawScriptInTextMode_AndEnvelopedInJsonMode()
    {
        string[] args =
        [
            "deploy", SampleTmdl, "--xmla", "-", "--deploy-full", "--skip-bpa", "--yes",
            "--non-interactive", "-s", "powerbi://api.powerbi.com/v1.0/myorg/Unreachable", "-d", "Target"
        ];

        var text = InvokeDeploy(args);
        // The engine's script, at the top level: a consumer pipes this straight to XMLA.
        Assert.Contains("createOrReplace", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"diagnostics\"", text, StringComparison.Ordinal);

        var json = JsonNode.Parse(InvokeDeploy([.. args, "--output-format", "json"]))!.AsObject();
        Assert.Equal(["data", "diagnostics"], json.Select(kvp => kvp.Key).Order());
        Assert.Equal("Target", json["data"]!["database"]!.GetValue<string>());
    }

    private static string InvokeDeploy(string[] args)
    {
        var services = TestServices.Create();
        var root = TestRoot.With(new DeployCommand(Providers, services.State).Build());
        var captured = ConsoleCapture.Invoke(root.Parse(args));

        Assert.True(captured.ExitCode == 0,
            $"'{string.Join(' ', args)}' exited {captured.ExitCode}: {captured.Stderr}");
        return captured.Stdout;
    }

    private static string Invoke(params string[] args)
    {
        var services = TestServices.Create();
        var root = TestRoot.With(args[0] == "get"
            ? new GetCommand(Providers, services.State).Build()
            : new LsCommand(Providers, services.State).Build());

        var captured = ConsoleCapture.Invoke(root.Parse(args));
        Assert.True(captured.ExitCode == 0,
            $"'{string.Join(' ', args)}' exited {captured.ExitCode}: {captured.Stderr}");
        return captured.Stdout;
    }
}
