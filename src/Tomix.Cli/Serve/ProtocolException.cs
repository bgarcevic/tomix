using System.Text.Json.Nodes;

namespace Tomix.Cli.Serve;

/// <summary>The JSON-RPC error codes of the session protocol (docs/protocol.md, Errors).</summary>
internal static class ProtocolErrors
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int NotInitialized = -32002;
    public const int RequestCancelled = -32800;

    /// <summary>Any tomix failure: <c>data.code</c> is its <c>TOMIX_*</c> code.</summary>
    public const int Tomix = -32000;
}

/// <summary>A request that fails with a JSON-RPC error.</summary>
internal sealed class ProtocolException(int code, string message, JsonObject? data = null) : Exception(message)
{
    public int Code { get; } = code;

    public JsonObject? ErrorData { get; } = data;

    /// <summary>A tomix failure, with the same code, hint and exit code the CLI reports.</summary>
    public static ProtocolException Tomix(string code, string message, string? hint, int exitCode)
        => new(ProtocolErrors.Tomix, message, new JsonObject
        {
            ["code"] = code,
            ["hint"] = hint,
            ["exitCode"] = exitCode
        });

    public static ProtocolException InvalidParams(string message, string? code = null)
        => new(ProtocolErrors.InvalidParams, message, code is null ? null : new JsonObject { ["code"] = code });
}
