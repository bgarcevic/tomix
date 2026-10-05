using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tomix.Tests.Support;

namespace Tomix.Cli.Tests;

/// <summary>
/// <c>docs/protocol.md</c> is the contract for <c>tx serve</c>, <c>tx mcp</c> and <c>tx ui</c>
/// clients (#348). Every method in its table has a request example answered by a response with
/// the same ID, every notification has an example, and every example is valid JSON-RPC 2.0 that
/// names only methods the table lists.
/// </summary>
public sealed partial class ProtocolDocTests
{
    private static readonly string Doc = File.ReadAllText(Path.Combine(RepoPaths.Root, "docs", "protocol.md"));

    private static readonly IReadOnlyList<(string Method, string Kind)> Methods = MethodTable();

    private static readonly IReadOnlyList<JsonObject> Messages = Examples()
        .Select(json => JsonNode.Parse(json))
        .OfType<JsonObject>()
        .Where(node => node.ContainsKey("jsonrpc"))
        .ToList();

    public static TheoryData<string> Requests => [.. Methods.Where(m => m.Kind == "request").Select(m => m.Method)];

    public static TheoryData<string> Notifications => [.. Methods.Where(m => m.Kind == "notification").Select(m => m.Method)];

    [Fact]
    public void TheMethodTable_ListsTheLifecycleAndEveryArea()
    {
        Assert.Contains(("initialize", "request"), Methods);
        Assert.Contains(("exit", "notification"), Methods);
        Assert.Contains(("model.changed", "notification"), Methods);
        Assert.True(Methods.Count >= 30, $"Only {Methods.Count} methods parsed from the table.");
        Assert.Equal(Methods.Count, Methods.Select(m => m.Method).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Requests))]
    public void EveryRequest_HasAnExampleAndAnAnswer(string method)
    {
        var request = Assert.Single(Messages, m => (string?)m["method"] == method && m.ContainsKey("id"));
        var id = request["id"]!.ToJsonString();

        var response = Assert.Single(Messages, m => !m.ContainsKey("method") && m["id"]?.ToJsonString() == id);
        Assert.True(response.ContainsKey("result") ^ response.ContainsKey("error"), $"Response {id} needs exactly one of result and error.");
    }

    [Theory]
    [MemberData(nameof(Notifications))]
    public void EveryNotification_HasAnExampleWithoutAnId(string method)
        => Assert.Contains(Messages, m => (string?)m["method"] == method && !m.ContainsKey("id"));

    [Fact]
    public void EveryExample_IsJsonRpc2_AndNamesOnlyListedMethods()
    {
        var listed = Methods.Select(m => m.Method).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(Messages);
        foreach (var message in Messages)
        {
            Assert.Equal("2.0", (string?)message["jsonrpc"]);
            if (message["method"] is { } method)
                Assert.Contains((string)method!, listed);
        }
    }

    [Fact]
    public void RequestIds_AreUnique_SoEachAnswerPairsWithOneRequest()
    {
        var ids = Messages.Where(m => m.ContainsKey("method") && m.ContainsKey("id")).Select(m => m["id"]!.ToJsonString()).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void SuccessfulResults_UseTheCliEnvelopePlusTheVersion()
    {
        var results = Messages
            .Where(m => m["result"] is JsonObject result && result.ContainsKey("data"))
            .Select(m => m["result"]!.AsObject())
            .ToList();

        Assert.NotEmpty(results);
        Assert.All(results, result =>
        {
            Assert.True(result.ContainsKey("diagnostics"), "A result envelope carries diagnostics.");
            Assert.True(result["version"] is JsonValue, "A result envelope carries the session version.");
        });
    }

    [Fact]
    public void TomixErrors_CarryTheirCodeAndHint()
    {
        var errors = Messages.Select(m => m["error"]).OfType<JsonObject>().ToList();

        var error = Assert.Single(errors);
        Assert.Equal(-32000, (int)error["code"]!);
        Assert.StartsWith("TOMIX_", (string?)error["data"]!["code"]);
        Assert.NotNull(error["data"]!["hint"]);
    }

    private static List<(string, string)> MethodTable()
    {
        var section = Doc[Doc.IndexOf("\n## Methods", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("\n### ", StringComparison.Ordinal)];
        return MethodRow().Matches(section).Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
    }

    private static IEnumerable<string> Examples()
        => JsonBlock().Matches(Doc).Select(m => m.Groups[1].Value);

    [GeneratedRegex(@"^\| `([^`]+)` \| (request|notification) \|", RegexOptions.Multiline)]
    private static partial Regex MethodRow();

    [GeneratedRegex(@"```json\r?\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex JsonBlock();
}
