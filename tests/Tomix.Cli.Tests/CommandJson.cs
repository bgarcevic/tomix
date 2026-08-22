using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tomix.Cli.Tests;

/// <summary>
/// Reads the stdout JSON contract: command payloads arrive wrapped as
/// <c>{ "data": …, "diagnostics": [] }</c> (see <c>Tomix.Cli.Output.CommandEnvelope{T}</c>), so a
/// test that wants the payload has to step through <c>data</c>.
/// </summary>
/// <remarks>
/// Going through here rather than writing <c>["data"]</c> at every assertion keeps the envelope in
/// one place: the day it changes, one helper moves instead of a dozen tests. It also asserts the
/// envelope is intact on the way past, so a command that forgets to wrap its output fails in the
/// test that reads it rather than silently returning a bare payload.
/// </remarks>
internal static class CommandJson
{
    /// <summary>The <c>data</c> payload of an enveloped command result.</summary>
    public static JsonNode Data(string stdout)
    {
        var root = JsonNode.Parse(stdout) ?? throw new InvalidOperationException("stdout was not JSON.");
        AssertEnveloped(root);
        return root["data"] ?? throw new InvalidOperationException("Envelope 'data' was null.");
    }

    /// <summary>The <c>data</c> payload as an array.</summary>
    public static JsonArray DataArray(string stdout) => Data(stdout).AsArray();

    /// <summary>
    /// The <c>data</c> payload as a <see cref="JsonDocument"/>, for tests already written against
    /// the <c>JsonElement</c> API. The caller owns the returned document.
    /// </summary>
    public static JsonDocument DataDocument(string stdout)
        => JsonDocument.Parse(Data(stdout).ToJsonString());

    private static void AssertEnveloped(JsonNode root)
    {
        var obj = root.AsObject();
        if (!obj.ContainsKey("data") || !obj.ContainsKey("diagnostics"))
        {
            throw new InvalidOperationException(
                "Command JSON is missing the data/diagnostics envelope. Render it through " +
                "CommandOutput.Render or CommandEnvelope<T>. Got keys: " +
                string.Join(", ", obj.Select(kvp => kvp.Key)));
        }
    }
}
