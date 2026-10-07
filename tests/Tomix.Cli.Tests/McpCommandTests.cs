using System.Text;
using System.Text.Json.Nodes;
using Tomix.App.Format;
using Tomix.Cli.Commands;
using Tomix.Cli.Serve;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;
using Tomix.Provider.Tom;
using Tomix.Tests.Support;

namespace Tomix.Cli.Tests;

/// <summary>
/// <c>tx mcp</c> driven through its stdio streams as an MCP client drives it (#354): a model opened,
/// changed, undone and saved through tools; <c>--read-only</c> listing only the tools that change
/// nothing; failures answered as tool errors the agent can act on; and the MCP lifecycle.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class McpCommandTests
{
    private static readonly IReadOnlyList<IModelProvider> Providers = [new TmdlModelProvider(), new TomFileModelProvider()];

    [Fact]
    public void AnAgent_OpensAModel_ChangesIt_UndoesAStep_AndSaves()
    {
        using var model = SampleModel.CopyToTemp();

        var run = Mcp(
            null,
            Initialize(),
            Initialized(),
            Call(2, "session_open", new JsonObject { ["model"] = model.Path }),
            Call(3, "object_set", new JsonObject { ["path"] = "Sales/Amount", ["set"] = new JsonObject { ["description"] = "Net" } }),
            Call(4, "object_set", new JsonObject { ["path"] = "Sales/Amount", ["set"] = new JsonObject { ["formatString"] = "0.0" } }),
            Call(5, "session_undo"),
            Call(6, "session_save"));

        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.Equal(Path.GetFullPath(model.Path), (string?)run.Tool(2)["data"]!["source"]);
        Assert.Equal(2, (int)run.Tool(4)["version"]!);
        Assert.Equal("object.set", (string?)run.Tool(5)["data"]!["label"]);
        Assert.Equal(true, (bool?)run.Tool(6)["data"]!["saved"]);

        var sales = File.ReadAllText(Path.Combine(model.Path, "tables", "Sales.tmdl"));
        Assert.Contains("/// Net", sales);
        Assert.DoesNotContain("formatString: 0.0", sales);
    }

    [Fact]
    public void ReadOnly_ListsOnlyTheToolsThatChangeNothing_AndRefusesTheOthers()
    {
        using var model = SampleModel.CopyToTemp();
        var before = File.ReadAllText(Path.Combine(model.Path, "tables", "Sales.tmdl"));

        var run = Mcp(
            ["mcp", model.Path, "--read-only"],
            Initialize(),
            Request(2, "tools/list"),
            Call(3, "object_get", new JsonObject { ["path"] = "Sales/Amount" }),
            Call(4, "object_set", new JsonObject { ["path"] = "Sales/Amount", ["set"] = new JsonObject { ["description"] = "Net" } }),
            Call(5, "session_save"));

        var tools = run.Result(2)["tools"]!.AsArray();
        Assert.NotEmpty(tools);
        Assert.All(tools, tool => Assert.Equal(true, (bool?)tool!["annotations"]!["readOnlyHint"]));
        Assert.DoesNotContain(tools, tool => (string?)tool!["name"] is "object_set" or "session_save" or "session_undo");
        Assert.Equal("Sales/Amount", (string?)run.Tool(3)["data"]!["path"]);
        Assert.Equal(-32602, (int)run.Error(4)["code"]!);
        Assert.Equal(-32602, (int)run.Error(5)["code"]!);
        Assert.Equal(before, File.ReadAllText(Path.Combine(model.Path, "tables", "Sales.tmdl")));
    }

    [Fact]
    public void EveryTool_SaysWhetherItWrites_AndReadOnlyListsExactlyTheOnesThatDoNot()
    {
        var all = Mcp(null, Initialize(), Request(2, "tools/list")).Result(2)["tools"]!.AsArray();
        var readOnly = Mcp(["mcp", "--read-only"], Initialize(), Request(2, "tools/list")).Result(2)["tools"]!.AsArray();

        Assert.All(all, tool =>
        {
            var annotations = tool!["annotations"]!;
            Assert.NotNull(annotations["readOnlyHint"]);
            Assert.NotNull(annotations["destructiveHint"]);
            Assert.Equal("object", (string?)tool["inputSchema"]!["type"]);
        });
        Assert.Equal(
            all.Where(tool => (bool)tool!["annotations"]!["readOnlyHint"]!).Select(tool => (string?)tool!["name"]),
            readOnly.Select(tool => (string?)tool!["name"]));
        Assert.Contains(all, tool => (string?)tool!["name"] == "object_remove" && (bool)tool["annotations"]!["destructiveHint"]!);
        Assert.Contains(all, tool => (string?)tool!["name"] == "object_add" && !(bool)tool["annotations"]!["readOnlyHint"]! && !(bool)tool["annotations"]!["destructiveHint"]!);
    }

    [Fact]
    public void Failures_ComeBackAsToolErrors_WithTheirCodeAndHint()
    {
        using var model = SampleModel.CopyToTemp();

        var run = Mcp(
            ["mcp", model.Path],
            Initialize(),
            Call(2, "object_get", new JsonObject { ["path"] = "Sales/Nope" }),
            Call(3, "object_get", new JsonObject { ["path"] = "Sales", ["all"] = true }),
            Call(4, "session_undo"),
            Call(5, "object_set", new JsonObject { ["path"] = "Sales/Amount", ["set"] = new JsonObject { ["description"] = new JsonObject() } }),
            Call(6, "no_such_tool"));

        var notFound = run.ToolError(2);
        Assert.Equal("TOMIX_OBJECT_NOT_FOUND", (string?)notFound["code"]);
        Assert.False(string.IsNullOrEmpty((string?)notFound["hint"]));
        var unknown = run.ToolError(3);
        Assert.Equal("TOMIX_MCP_INVALID_ARGUMENT", (string?)unknown["code"]);
        Assert.Contains("'all'", (string?)unknown["error"]);
        Assert.Equal("TOMIX_SESSION_NOTHING_TO_UNDO", (string?)run.ToolError(4)["code"]);
        Assert.Equal("TOMIX_MCP_INVALID_ARGUMENT", (string?)run.ToolError(5)["code"]);
        Assert.Equal(-32602, (int)run.Error(6)["code"]!);
    }

    [Fact]
    public void ATransaction_GroupsEdits_IntoOneLabelledUndoStep()
    {
        using var model = SampleModel.CopyToTemp();

        var run = Mcp(
            ["mcp", model.Path],
            Initialize(),
            Call(2, "transaction_begin", new JsonObject { ["label"] = "Margins" }),
            Call(3, "object_add", new JsonObject { ["path"] = "Sales/A", ["type"] = "Measure", ["expression"] = "1" }),
            Call(4, "object_add", new JsonObject { ["path"] = "Sales/B", ["type"] = "Measure", ["expression"] = "2" }),
            Call(5, "transaction_commit"),
            Call(6, "session_history"));

        var step = Assert.Single(run.Tool(6)["data"]!["steps"]!.AsArray());
        Assert.Equal("Margins", (string?)step!["label"]);
    }

    [Fact]
    public void Lifecycle_NegotiatesTheVersion_AndAnswersOnlyPingBeforeInitialize()
    {
        var run = Mcp(
            null,
            Request(1, "tools/list"),
            Request(2, "ping"),
            Request(3, "initialize", new JsonObject { ["protocolVersion"] = "1999-01-01", ["clientInfo"] = new JsonObject { ["name"] = "test" } }),
            Request(4, "initialize"),
            Request(5, "resources/list"),
            Call(6, "session_status"));

        Assert.Equal(-32002, (int)run.Error(1)["code"]!);
        Assert.Empty(run.Result(2));
        var initialize = run.Result(3);
        Assert.Equal(McpServer.ProtocolVersions[0], (string?)initialize["protocolVersion"]);
        Assert.Equal("tomix", (string?)initialize["serverInfo"]!["name"]);
        Assert.False((bool)initialize["capabilities"]!["tools"]!["listChanged"]!);
        Assert.Contains("session_save", (string?)initialize["instructions"]);
        Assert.Equal(-32600, (int)run.Error(4)["code"]!);
        Assert.Equal(-32601, (int)run.Error(5)["code"]!);
        Assert.Equal("TOMIX_SESSION_NO_MODEL", (string?)run.ToolError(6)["code"]);
    }

    [Theory]
    [InlineData("2025-06-18")]
    [InlineData("2024-11-05")]
    public void Initialize_AnswersWithTheClientsVersion_WhenItSpeaksIt(string version)
    {
        var run = Mcp(null, Request(1, "initialize", new JsonObject { ["protocolVersion"] = version }));

        Assert.Equal(version, (string?)run.Result(1)["protocolVersion"]);
    }

    [Fact]
    public void Input_StartingWithAByteOrderMark_IsRead()
    {
        // What Windows PowerShell pipes: '...' | tx mcp.
        var run = Mcp(null, "\uFEFF" + Initialize(), Request(2, "tools/list"));

        Assert.Equal("tomix", (string?)run.Result(1)["serverInfo"]!["name"]);
        Assert.NotEmpty(run.Result(2)["tools"]!.AsArray());
    }

    [Fact]
    public void Stdout_CarriesOnlyMessages_OneAPerLine()
    {
        using var model = SampleModel.CopyToTemp();

        var run = Mcp(
            ["mcp", model.Path],
            Initialize(),
            Call(2, "bpa_run"),
            Call(3, "dax_format", new JsonObject { ["expression"] = "sum(Sales[Amount])" }));

        Assert.Equal("", run.Stdout);
        Assert.NotNull(run.Tool(2)["data"]!["rulesEvaluated"]);
        Assert.Contains("SUM", (string?)run.Tool(3)["data"]!["formatted"]);
    }

    internal static string Initialize(int id = 1, string name = "test")
        => Request(id, "initialize", new JsonObject
        {
            ["protocolVersion"] = McpServer.ProtocolVersions[0],
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = name, ["version"] = "1.0" }
        });

    internal static string Initialized() => Line(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });

    internal static string Call(int id, string tool, JsonObject? arguments = null)
    {
        var parameters = new JsonObject { ["name"] = tool };
        if (arguments is not null)
            parameters["arguments"] = arguments;
        return Request(id, "tools/call", parameters);
    }

    internal static string Request(int id, string method, JsonObject? parameters = null)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null)
            message["params"] = parameters;
        return Line(message);
    }

    private static string Line(JsonObject message) => message.ToJsonString() + "\n";

    internal static McpRun Mcp(string[]? args, params string[] lines)
    {
        var root = Program.BuildRootCommand(Providers, new CompositeExpressionFormatterClient([new OfflineDaxFormatterClient()]), TestRoot.Version, TestServices.Create());
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Concat(lines)));
        using var output = new MemoryStream();
        McpCommand.TestStreams.Value = (input, output);
        try
        {
            var captured = ConsoleCapture.InvokeThroughProgram(root.Parse(args ?? ["mcp"]));
            return new McpRun(captured.ExitCode, captured.Stdout, captured.Stderr, McpRun.ReadLines(output.ToArray()));
        }
        finally
        {
            McpCommand.TestStreams.Value = null;
        }
    }
}

/// <summary>What a <c>tx mcp</c> run wrote: its exit code, the console streams, and its messages.</summary>
internal sealed record McpRun(int ExitCode, string Stdout, string Stderr, IReadOnlyList<JsonObject> Messages)
{
    public JsonObject Result(int id)
        => Assert.Single(Messages, message => (int?)message["id"] == id && message.ContainsKey("result"))["result"]!.AsObject();

    public JsonObject Error(int id)
        => Assert.Single(Messages, message => (int?)message["id"] == id && message.ContainsKey("error"))["error"]!.AsObject();

    /// <summary>The JSON a successful tool call returned.</summary>
    public JsonObject Tool(int id)
    {
        var result = Result(id);
        Assert.True((bool?)result["isError"] == false, result.ToJsonString());
        return Text(result);
    }

    /// <summary>The error a failed tool call returned: <c>error</c>, <c>code</c> and <c>hint</c>.</summary>
    public JsonObject ToolError(int id)
    {
        var result = Result(id);
        Assert.True((bool?)result["isError"] == true, result.ToJsonString());
        return Text(result);
    }

    private static JsonObject Text(JsonObject result)
    {
        var content = Assert.Single(result["content"]!.AsArray())!;
        Assert.Equal("text", (string?)content["type"]);
        return JsonNode.Parse((string)content["text"]!)!.AsObject();
    }

    /// <summary>Splits the server's output into messages: one JSON object per line.</summary>
    public static List<JsonObject> ReadLines(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        Assert.True(text.Length == 0 || text.EndsWith('\n'), "The last message does not end its line.");
        var messages = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!.AsObject()).ToList();
        Assert.All(messages, message => Assert.Equal("2.0", (string?)message["jsonrpc"]));
        return messages;
    }
}
