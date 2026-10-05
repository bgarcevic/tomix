using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tomix.Tests.Support;
using static Tomix.Cli.Tests.ServeCommandTests;

namespace Tomix.Cli.Tests;

/// <summary>
/// Approved snapshots of what <c>tx serve</c> offers and answers (#349, ADR 0001 §5): the methods
/// and notifications <c>initialize</c> advertises, and a scripted session's messages, so a change
/// to a payload a client depends on shows up in review. Regenerate both with
/// <c>.\scripts\dev.ps1 snapshot</c> (Windows) or <c>./scripts/dev.sh snapshot</c> (macOS/Linux).
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed partial class ServeSurfaceTests
{
    /// <summary>In the spec's method table but not served yet; <c>capabilities</c> leaves them out.</summary>
    private static readonly string[] NotYetServed = ["query.run", "$/progress", "diagnostics.updated"];

    /// <summary>Handled by the protocol layer itself rather than advertised.</summary>
    private static readonly string[] Lifecycle = ["initialize", "shutdown", "exit", "$/cancelRequest"];

    [Fact]
    public void Capabilities_MatchApprovedSnapshot()
    {
        var capabilities = Capabilities();
        var actual = new StringBuilder();
        foreach (var (heading, names) in new[] { ("methods", capabilities.Methods), ("notifications", capabilities.Notifications) })
        {
            actual.Append("# ").Append(heading).Append('\n');
            foreach (var name in names)
                actual.Append(name).Append('\n');
        }

        Approve("ServeSurface.approved.txt", actual.ToString());
    }

    [Fact]
    public void Capabilities_AreTheSpecsMethods_LessTheOnesNotServedYet()
    {
        var spec = SpecMethods();
        var capabilities = Capabilities();
        string[] served = [.. capabilities.Methods, .. capabilities.Notifications];

        Assert.Empty(served.Except(spec));
        Assert.Equal(
            spec.Except(Lifecycle).Except(NotYetServed).Order(StringComparer.Ordinal),
            served.Order(StringComparer.Ordinal));
        Assert.Empty(NotYetServed.Except(spec));
    }

    [Fact]
    public void ScriptedSession_MatchesApprovedTranscript()
    {
        using var model = SampleModel.CopyToTemp();

        var run = ServeModel(
            null,
            Initialize(),
            Request(2, "session.open", new JsonObject { ["model"] = model.Path }),
            Request(3, "model.tree"),
            Request(4, "model.tree", new JsonObject { ["path"] = "Sales" }),
            Request(5, "object.add", new JsonObject { ["path"] = "Sales/Margin", ["type"] = "Measure", ["expression"] = "[Total Sales] * 0.1" }),
            Request(6, "object.set", new JsonObject { ["path"] = "Sales/Margin", ["set"] = new JsonObject { ["formatString"] = "0.0%" } }),
            Request(7, "object.get", new JsonObject { ["path"] = "Sales/Margin" }),
            Request(8, "session.status"),
            Request(9, "session.history"),
            Request(10, "session.undo"),
            Request(11, "session.snapshot"),
            Request(12, "session.close", new JsonObject { ["discard"] = true }),
            Request(13, "shutdown"),
            Exit());

        var transcript = new StringBuilder();
        foreach (var message in run.Messages)
            transcript.Append(message.ToJsonString()).Append('\n');
        var normalized = transcript.ToString()
            .Replace(System.Text.Json.JsonSerializer.Serialize(model.Path)[1..^1], "<model>", StringComparison.Ordinal)
            .Replace($"\"{Path.GetFileName(model.Path)}\"", "\"<name>\"", StringComparison.Ordinal);
        normalized = DurationRegex().Replace(normalized, "\"durationMs\":0");

        Approve("ServeTranscript.approved.txt", normalized);
    }

    private static (string[] Methods, string[] Notifications) Capabilities()
    {
        var run = ServeCommandTests.Serve(Initialize());
        var capabilities = run.Result(1)["capabilities"]!;
        return (
            [.. capabilities["methods"]!.AsArray().Select(item => (string)item!)],
            [.. capabilities["notifications"]!.AsArray().Select(item => (string)item!)]);
    }

    private static string[] SpecMethods()
    {
        var doc = File.ReadAllText(RepoPaths.Combine("docs", "protocol.md"));
        return [.. MethodRow().Matches(doc).Select(match => match.Groups[1].Value)];
    }

    private static void Approve(string name, string actual, [CallerFilePath] string source = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(source)!, name);
        if (Environment.GetEnvironmentVariable("TOMIX_UPDATE_SNAPSHOTS") == "1")
        {
            File.WriteAllText(path, actual);
            return;
        }

        var approved = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : "";
        Assert.True(
            approved == actual,
            $"{name} differs from what tx serve answers. If the change is intended, update docs/protocol.md and " +
            "regenerate: .\\scripts\\dev.ps1 snapshot (Windows) or ./scripts/dev.sh snapshot (macOS/Linux)." + Environment.NewLine +
            FirstDifference(approved, actual));
    }

    private static string FirstDifference(string approved, string actual)
    {
        var expected = approved.Split('\n');
        var lines = actual.Split('\n');
        for (var i = 0; i < Math.Max(expected.Length, lines.Length); i++)
        {
            var left = i < expected.Length ? expected[i] : "<none>";
            var right = i < lines.Length ? lines[i] : "<none>";
            if (left != right)
                return $"Line {i + 1}:{Environment.NewLine}  approved: {left}{Environment.NewLine}  actual:   {right}";
        }

        return "";
    }

    [GeneratedRegex(@"^\| `([^`]+)` \| (?:request|notification) \|", RegexOptions.Multiline)]
    private static partial Regex MethodRow();

    [GeneratedRegex("\"durationMs\":\\d+")]
    private static partial Regex DurationRegex();
}
