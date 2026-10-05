using System.Text;
using System.Text.Json.Nodes;
using Tomix.App;
using Tomix.App.Format;
using Tomix.Cli.Commands;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;
using Tomix.Provider.Tom;
using Tomix.Tests.Support;

namespace Tomix.Cli.Tests;

/// <summary>
/// <c>tx serve</c> driven through its stdio streams, framed exactly as a client frames them (#349):
/// a session opened, edited, undone and saved, with the events a client sees; malformed input
/// answered without stopping the server; and the end of input ending it cleanly.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class ServeCommandTests
{
    private static readonly IReadOnlyList<IModelProvider> Providers = [new TmdlModelProvider(), new TomFileModelProvider()];

    [Fact]
    public void SetUndoSave_AnswersEachRequest_AndAnnouncesTheChanges()
    {
        using var model = SampleModel.CopyToTemp();

        var run = Serve(
            Initialize(),
            Request(2, "session.open", new JsonObject { ["model"] = model.Path }),
            Request(3, "object.set", new JsonObject { ["path"] = "Sales/Amount", ["set"] = new JsonObject { ["description"] = "Net" } }),
            Request(4, "object.set", new JsonObject { ["path"] = "Sales/Amount", ["set"] = new JsonObject { ["formatString"] = "0.0" } }),
            Request(5, "session.undo"),
            Request(6, "session.save"),
            Request(7, "shutdown"),
            Exit());

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("Sales/Amount", (string?)run.Result(3)["data"]!["set"]);
        Assert.Equal(2, (int)run.Result(4)["version"]!);
        Assert.Equal("t2", (string?)run.Result(5)["data"]!["transaction"]);
        Assert.Equal(true, (bool?)run.Result(6)["data"]!["saved"]);

        var changes = run.Notifications("model.changed");
        Assert.Equal([1L, 2L, 3L], changes.Select(change => (long)change["version"]!));
        Assert.Equal(["apply", "apply", "undo"], changes.Select(change => (string?)change["origin"]!["kind"]));
        Assert.All(changes, change => Assert.Equal("test-1", (string?)change["origin"]!["client"]));
        Assert.Equal("Sales/Amount", (string?)changes[0]["changes"]![0]!["path"]);

        var saved = Assert.Single(run.Notifications("session.saved"));
        Assert.Equal(3, (int)saved["version"]!);
        Assert.Contains(run.Notifications("session.state"), state => (string?)state["state"] == "dirty");
        Assert.Equal("clean", (string?)run.Notifications("session.state").Last(state => (string?)state["state"] != "closed")["state"]);

        var sales = File.ReadAllText(Path.Combine(model.Path, "tables", "Sales.tmdl"));
        Assert.Contains("/// Net", sales);
        Assert.DoesNotContain("formatString: 0.0", sales);
    }

    [Fact]
    public void ModelGivenAtStart_IsOpenWithoutSessionOpen()
    {
        using var model = SampleModel.CopyToTemp();

        var run = ServeModel(model.Path, Initialize(), Request(2, "model.summary"));

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(3, (int)run.Result(2)["data"]!["counts"]!["tables"]!);
        Assert.Equal(0, (int)run.Result(2)["version"]!);
    }

    [Fact]
    public void MalformedInput_IsAnswered_AndTheServerGoesOn()
    {
        var run = Serve(
            Frame("{not json"),
            "Content-Type: application/json\r\n\r\n",
            Frame("""[{ "jsonrpc": "2.0", "id": 9, "method": "initialize" }]"""),
            Frame("""{ "id": 1, "method": "initialize" }"""),
            Frame("""{ "jsonrpc": "2.0", "id": true, "method": "initialize" }"""),
            Frame("""{ "jsonrpc": "2.0", "id": 2, "method": 5 }"""),
            Frame("""{ "jsonrpc": "2.0", "id": 3, "method": "initialize", "params": [1] }"""),
            Initialize(id: 4),
            Request(5, "no.such.method"),
            Request(6, "shutdown"),
            Exit());

        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.Equal([-32700, -32600, -32600, -32600], run.Errors(id: null).Select(error => (int)error["code"]!));
        Assert.Equal(-32600, (int)run.Error(1)["code"]!);
        Assert.Equal(-32600, (int)run.Error(2)["code"]!);
        Assert.Equal(-32602, (int)run.Error(3)["code"]!);
        Assert.Equal("0", (string?)run.Result(4)["protocolVersion"]);
        Assert.Equal(-32601, (int)run.Error(5)["code"]!);
        Assert.Contains(run.Messages, message => (int?)message["id"] == 6 && message.ContainsKey("result"));
        Assert.Single(run.Errors(id: null), error => (string?)error["message"] == "The frame has no Content-Length header.");
        Assert.Single(run.Errors(id: null), error => (string?)error["message"] == "Batch requests are not supported in protocol v0.");
    }

    [Fact]
    public void RequestsBeforeInitialize_FailWithNotInitialized()
    {
        var run = Serve(Request(1, "session.status"), Initialize(id: 2), Request(3, "initialize"));

        Assert.Equal(-32002, (int)run.Error(1)["code"]!);
        Assert.Equal("test-1", (string?)run.Result(2)["clientId"]);
        Assert.Equal(-32600, (int)run.Error(3)["code"]!);
    }

    [Fact]
    public void AnotherProtocolVersion_IsRefused_WithTheSupportedOnes()
    {
        var run = Serve(Request(1, "initialize", new JsonObject { ["protocolVersion"] = "7" }));

        var error = run.Error(1);
        Assert.Equal(-32000, (int)error["code"]!);
        Assert.Equal("TOMIX_PROTOCOL_VERSION", (string?)error["data"]!["code"]);
        Assert.Equal("0", (string?)error["data"]!["supported"]![0]);
    }

    [Fact]
    public void TomixFailures_CarryTheirCodeHintAndExitCode()
    {
        using var model = SampleModel.CopyToTemp();

        var run = ServeModel(
            model.Path,
            Initialize(),
            Request(2, "object.get", new JsonObject { ["path"] = "Sales/Nope" }),
            Request(3, "session.undo"),
            Request(4, "object.set", new JsonObject { ["path"] = "Sales/Amount", ["save"] = true }),
            Request(5, "object.find", new JsonObject { ["pattern"] = "Amount", ["inScope"] = "names" }),
            Request(6, "object.get", new JsonObject { ["path"] = "Sales", ["id"] = "o1" }));

        var notFound = run.Error(2);
        Assert.Equal(-32000, (int)notFound["code"]!);
        Assert.Equal("TOMIX_OBJECT_NOT_FOUND", (string?)notFound["data"]!["code"]);
        Assert.False(string.IsNullOrEmpty((string?)notFound["data"]!["hint"]));
        Assert.Equal(1, (int)notFound["data"]!["exitCode"]!);
        Assert.Equal("TOMIX_SESSION_NOTHING_TO_UNDO", (string?)run.Error(3)["data"]!["code"]);
        Assert.Equal(-32602, (int)run.Error(4)["code"]!);
        Assert.Equal(-32602, (int)run.Error(5)["code"]!);
        Assert.Equal(-32602, (int)run.Error(6)["code"]!);
    }

    [Fact]
    public void ModelMethods_WithNoModelOpen_FailWithNoModel()
    {
        var run = Serve(Initialize(), Request(2, "object.get", new JsonObject { ["path"] = "Sales" }));

        Assert.Equal("TOMIX_SESSION_NO_MODEL", (string?)run.Error(2)["data"]!["code"]);
    }

    [Fact]
    public void Transaction_IsAnnounced_AndCommitsAsOneChange()
    {
        using var model = SampleModel.CopyToTemp();

        var run = ServeModel(
            model.Path,
            Initialize(),
            Request(2, "transaction.begin", new JsonObject { ["label"] = "Margins" }),
            Request(3, "object.add", new JsonObject { ["path"] = "Sales/A", ["type"] = "Measure", ["expression"] = "1" }),
            Request(4, "object.add", new JsonObject { ["path"] = "Sales/B", ["type"] = "Measure", ["expression"] = "2" }),
            Request(5, "transaction.commit"),
            Request(6, "session.history"));

        var opened = Assert.Single(run.Notifications("transaction.opened"));
        Assert.Equal("Margins", (string?)opened["label"]);
        Assert.Equal("test-1", (string?)opened["client"]);
        var closed = Assert.Single(run.Notifications("transaction.closed"));
        Assert.Equal("committed", (string?)closed["outcome"]);
        Assert.Equal((string?)opened["transaction"], (string?)closed["transaction"]);

        var change = Assert.Single(run.Notifications("model.changed"));
        Assert.Equal(["Sales/A", "Sales/B"], change["changes"]!.AsArray().Select(item => (string?)item!["path"]));
        var step = Assert.Single(run.Result(6)["data"]!["steps"]!.AsArray());
        Assert.Equal("Margins", (string?)step!["label"]);
    }

    [Fact]
    public void Objects_CanBeAddressedById_AndKeepItThroughARename()
    {
        using var model = SampleModel.CopyToTemp();

        var tree = ServeModel(model.Path, Initialize(), Request(2, "model.tree", new JsonObject { ["path"] = "Sales" }));
        var amount = tree.Result(2)["data"]!["children"]!.AsArray().First(child => (string?)child!["path"] == "Sales/Amount")!;
        var id = (string)amount["id"]!;

        var run = ServeModel(
            model.Path,
            Initialize(),
            Request(2, "object.get", new JsonObject { ["id"] = id }),
            Request(3, "object.move", new JsonObject { ["id"] = id, ["to"] = "Sales/Net Amount" }),
            Request(4, "object.get", new JsonObject { ["id"] = id }),
            Request(5, "object.get", new JsonObject { ["id"] = "o999" }));

        Assert.Equal("Sales/Amount", (string?)run.Result(2)["data"]!["path"]);
        Assert.Equal(id, (string?)run.Result(2)["data"]!["id"]);
        Assert.Equal("Sales/Net Amount", (string?)run.Result(4)["data"]!["path"]);
        Assert.Equal(id, (string?)run.Result(4)["data"]!["id"]);
        Assert.Equal("TOMIX_OBJECT_NOT_FOUND", (string?)run.Error(5)["data"]!["code"]);
    }

    [Fact]
    public void Snapshot_ListsEveryObjectWithItsId()
    {
        using var model = SampleModel.CopyToTemp();

        var run = ServeModel(model.Path, Initialize(), Request(2, "session.snapshot"), Request(3, "model.tree"));

        var objects = run.Result(2)["data"]!["objects"]!.AsArray();
        Assert.Contains(objects, item => (string?)item!["path"] == "Sales/Amount" && (string?)item["type"] == "Column");
        Assert.All(objects, item => Assert.StartsWith("o", (string?)item!["id"]));
        Assert.Equal(objects.Count, objects.Select(item => (string?)item!["id"]).Distinct().Count());

        var top = run.Result(3)["data"]!;
        Assert.Null(top["parent"]);
        Assert.Contains(top["children"]!.AsArray(), child => (string?)child!["path"] == "Sales" && (bool)child["hasChildren"]!);
    }

    [Fact]
    public void Close_WithUnsavedChanges_NeedsSaveOrDiscard()
    {
        using var model = SampleModel.CopyToTemp();
        var before = File.ReadAllText(Path.Combine(model.Path, "tables", "Sales.tmdl"));

        var run = ServeModel(
            model.Path,
            Initialize(),
            Request(2, "object.add", new JsonObject { ["path"] = "Sales/A", ["type"] = "Measure", ["expression"] = "1" }),
            Request(3, "session.close"),
            Request(4, "session.close", new JsonObject { ["discard"] = true }),
            Request(5, "session.status"));

        Assert.Equal("TOMIX_SESSION_DIRTY", (string?)run.Error(3)["data"]!["code"]);
        Assert.Equal(true, (bool?)run.Result(4)["data"]!["closed"]);
        Assert.Equal(false, (bool?)run.Result(4)["data"]!["saved"]);
        Assert.Equal("closed", (string?)run.Notifications("session.state").Last()["state"]);
        Assert.Equal("TOMIX_SESSION_NO_MODEL", (string?)run.Error(5)["data"]!["code"]);
        Assert.Equal(before, File.ReadAllText(Path.Combine(model.Path, "tables", "Sales.tmdl")));
    }

    [Fact]
    public void Open_WithUnsavedChanges_NeedsDiscard()
    {
        using var model = SampleModel.CopyToTemp();

        var run = ServeModel(
            model.Path,
            Initialize(),
            Request(2, "object.add", new JsonObject { ["path"] = "Sales/A", ["type"] = "Measure", ["expression"] = "1" }),
            Request(3, "session.open", new JsonObject { ["model"] = model.Path }),
            Request(4, "object.get", new JsonObject { ["path"] = "Sales/A" }),
            Request(5, "session.open", new JsonObject { ["model"] = model.Path, ["discard"] = true }),
            Request(6, "object.get", new JsonObject { ["path"] = "Sales/A" }));

        Assert.Equal("TOMIX_SESSION_DIRTY", (string?)run.Error(3)["data"]!["code"]);
        Assert.Equal("Sales/A", (string?)run.Result(4)["data"]!["path"]);
        Assert.NotNull(run.Result(5));
        Assert.Equal("TOMIX_OBJECT_NOT_FOUND", (string?)run.Error(6)["data"]!["code"]);
    }

    [Fact]
    public void PositionalValues_StartingWithADash_AreNotReadAsOptions()
    {
        using var model = SampleModel.CopyToTemp();

        var run = ServeModel(model.Path, Initialize(), Request(2, "object.find", new JsonObject { ["pattern"] = "--regex" }));

        Assert.NotNull(run.Result(2)["data"]);
    }

    [Fact]
    public void EndOfInput_WithAnOpenTransaction_RollsItBack()
    {
        using var model = SampleModel.CopyToTemp();
        var before = File.ReadAllText(Path.Combine(model.Path, "tables", "Sales.tmdl"));

        var run = ServeModel(
            model.Path,
            Initialize(),
            Request(2, "transaction.begin", new JsonObject { ["label"] = "left open" }),
            Request(3, "object.add", new JsonObject { ["path"] = "Sales/A", ["type"] = "Measure", ["expression"] = "1" }));

        Assert.Equal(0, run.ExitCode);
        Assert.NotNull(run.Result(3));
        Assert.Contains("rolled back", run.Stderr);
        Assert.DoesNotContain("unsaved changes", run.Stderr);
        Assert.Equal(before, File.ReadAllText(Path.Combine(model.Path, "tables", "Sales.tmdl")));
    }

    [Fact]
    public void EndOfInput_WithoutShutdown_StopsAndDiscardsUnsavedWork()
    {
        using var model = SampleModel.CopyToTemp();
        var before = File.ReadAllText(Path.Combine(model.Path, "tables", "Sales.tmdl"));

        var run = ServeModel(
            model.Path,
            Initialize(),
            Request(2, "object.add", new JsonObject { ["path"] = "Sales/A", ["type"] = "Measure", ["expression"] = "1" }));

        Assert.Equal(0, run.ExitCode);
        Assert.NotNull(run.Result(2));
        Assert.Contains("unsaved changes", run.Stderr);
        Assert.Equal(before, File.ReadAllText(Path.Combine(model.Path, "tables", "Sales.tmdl")));
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void Exit_IsZeroOnlyAfterShutdown(bool shutdown, int exitCode)
    {
        var run = shutdown
            ? Serve(Initialize(), Request(2, "shutdown"), Request(3, "session.status"), Exit(), Request(4, "session.status"))
            : Serve(Initialize(), Exit(), Request(4, "session.status"));

        Assert.Equal(exitCode, run.ExitCode);
        Assert.DoesNotContain(run.Messages, message => (int?)message["id"] == 4);
        if (shutdown)
            Assert.Equal(-32600, (int)run.Error(3)["code"]!);
    }

    [Fact]
    public void Stdout_CarriesOnlyFrames()
    {
        using var model = SampleModel.CopyToTemp();

        var run = ServeModel(
            model.Path,
            Initialize(),
            Request(2, "bpa.run"),
            Request(3, "dax.format", new JsonObject { ["expression"] = "sum(Sales[Amount])" }),
            Request(4, "shutdown"),
            Exit());

        Assert.Equal("", run.Stdout);
        Assert.NotNull(run.Result(2)["data"]!["rulesEvaluated"]);
        Assert.Contains("SUM", (string?)run.Result(3)["data"]!["formatted"]);
    }

    internal static string Initialize(int id = 1)
        => Request(id, "initialize", new JsonObject { ["protocolVersion"] = "0", ["clientInfo"] = new JsonObject { ["name"] = "test" } });

    internal static string Request(int id, string method, JsonObject? parameters = null)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null)
            message["params"] = parameters;
        return Frame(message.ToJsonString());
    }

    internal static string Exit() => Frame("""{ "jsonrpc": "2.0", "method": "exit" }""");

    internal static string Frame(string body) => $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";

    internal static ServeRun Serve(params string[] frames) => ServeModel(null, frames);

    internal static ServeRun ServeModel(string? modelPath, params string[] frames)
    {
        var root = Program.BuildRootCommand(Providers, new CompositeExpressionFormatterClient([new OfflineDaxFormatterClient()]), TestRoot.Version, TestServices.Create());
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Concat(frames)));
        using var output = new MemoryStream();
        ServeCommand.TestStreams.Value = (input, output);
        try
        {
            string[] args = modelPath is null ? ["serve"] : ["serve", modelPath];
            var captured = ConsoleCapture.InvokeThroughProgram(root.Parse(args));
            return new ServeRun(captured.ExitCode, captured.Stdout, captured.Stderr, ServeRun.ReadFrames(output.ToArray()));
        }
        finally
        {
            ServeCommand.TestStreams.Value = null;
        }
    }
}

/// <summary>What a <c>tx serve</c> run wrote: its exit code, the console streams, and the messages it framed.</summary>
internal sealed record ServeRun(int ExitCode, string Stdout, string Stderr, IReadOnlyList<JsonObject> Messages)
{
    public JsonObject Result(int id)
        => Assert.Single(Messages, message => (int?)message["id"] == id && message.ContainsKey("result"))["result"] as JsonObject
           ?? throw new Xunit.Sdk.XunitException($"Request {id} has a null result.");

    public JsonObject Error(int id)
        => Assert.Single(Messages, message => message["id"] is JsonValue value && (int)value == id && message.ContainsKey("error"))["error"]!.AsObject();

    public IEnumerable<JsonObject> Errors(int? id)
        => Messages.Where(message => message.ContainsKey("error") && (id is null ? message["id"] is null : (int?)message["id"] == id))
            .Select(message => message["error"]!.AsObject());

    public List<JsonObject> Notifications(string method)
        => Messages.Where(message => (string?)message["method"] == method).Select(message => message["params"]!.AsObject()).ToList();

    /// <summary>Splits the server's output into messages, checking each frame's Content-Length.</summary>
    public static List<JsonObject> ReadFrames(byte[] bytes)
    {
        var messages = new List<JsonObject>();
        var position = 0;
        while (position < bytes.Length)
        {
            var headerEnd = IndexOf(bytes, "\r\n\r\n"u8.ToArray(), position);
            Assert.True(headerEnd >= 0, "A frame has no blank line after its headers.");
            var header = Encoding.ASCII.GetString(bytes, position, headerEnd - position);
            Assert.StartsWith("Content-Length: ", header);
            var length = int.Parse(header["Content-Length: ".Length..], System.Globalization.CultureInfo.InvariantCulture);
            var body = Encoding.UTF8.GetString(bytes, headerEnd + 4, length);
            messages.Add(JsonNode.Parse(body)!.AsObject());
            position = headerEnd + 4 + length;
        }

        Assert.All(messages, message => Assert.Equal("2.0", (string?)message["jsonrpc"]));
        return messages;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (var i = start; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        return -1;
    }
}
