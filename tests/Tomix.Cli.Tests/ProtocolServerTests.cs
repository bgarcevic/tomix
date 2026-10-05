using System.Text;
using System.Text.Json.Nodes;
using Tomix.Cli.Serve;
using static Tomix.Cli.Tests.ServeCommandTests;

namespace Tomix.Cli.Tests;

/// <summary>
/// The protocol layer of <c>tx serve</c> with stand-in methods: <c>$/cancelRequest</c> reaches a
/// request that is running and one still queued behind it, and both are answered with -32800.
/// </summary>
public sealed class ProtocolServerTests
{
    [Fact]
    public async Task CancelRequest_StopsTheRunningRequest()
    {
        var methods = new StubMethods();

        var messages = await RunAsync(methods, Initialize(), Request(2, "slow"), Cancel(2), Request(3, "shutdown"), Exit());

        // Cancelled while running or before it started: either way it is answered -32800, never "late".
        Assert.Equal(-32800, ErrorCode(messages, 2));
        Assert.DoesNotContain("fast", methods.Started);
    }

    [Fact]
    public async Task CancelRequest_DropsAQueuedRequest_BeforeItStarts()
    {
        var methods = new StubMethods();

        // 'slow' holds the queue until its own cancellation, which arrives after the one for 'fast'.
        var messages = await RunAsync(methods, Initialize(), Request(2, "slow"), Request(3, "fast"), Cancel(3), Cancel(2), Request(4, "fast"));

        Assert.Equal(-32800, ErrorCode(messages, 2));
        Assert.Equal(-32800, ErrorCode(messages, 3));
        Assert.Equal("done", (string?)messages.Single(message => (int?)message["id"] == 4)["result"]);
        Assert.Single(methods.Started, method => method == "fast");
    }

    [Fact]
    public async Task AFailingMethod_IsAnInternalError_AndTheServerGoesOn()
    {
        var messages = await RunAsync(new StubMethods(), Initialize(), Request(2, "throws"), Request(3, "fast"));

        var error = messages.Single(message => (int?)message["id"] == 2)["error"]!;
        Assert.Equal(-32603, (int)error["code"]!);
        Assert.Equal("TOMIX_UNEXPECTED", (string?)error["data"]!["code"]);
        Assert.Equal("done", (string?)messages.Single(message => (int?)message["id"] == 3)["result"]);
    }

    private static string Cancel(int id)
        => Frame(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "$/cancelRequest", ["params"] = new JsonObject { ["id"] = id } }.ToJsonString());

    private static int ErrorCode(List<JsonObject> messages, int id)
        => (int)messages.Single(message => (int?)message["id"] == id)["error"]!["code"]!;

    private static async Task<List<JsonObject>> RunAsync(IProtocolMethods methods, params string[] frames)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Concat(frames)));
        using var output = new MemoryStream();
        await new ProtocolServer(input, output, methods, "1.0.0", TextWriter.Null).RunAsync(CancellationToken.None);
        return ServeRun.ReadFrames(output.ToArray());
    }

    private sealed class StubMethods : IProtocolMethods
    {
        public List<string> Started { get; } = [];

        public IReadOnlyList<string> Methods => ["slow", "fast", "throws"];

        public IReadOnlyList<string> Notifications => [];

        public void Attach(string clientId, Action<string, JsonNode?> notify)
        {
        }

        public async Task<JsonNode?> InvokeAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
        {
            Started.Add(method);
            switch (method)
            {
                case "slow":
                    // Long enough to fail the test rather than hang it when cancellation never comes.
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                    return "late";
                case "throws":
                    throw new InvalidOperationException("boom");
                default:
                    return "done";
            }
        }

        public ValueTask DetachAsync() => ValueTask.CompletedTask;
    }
}
