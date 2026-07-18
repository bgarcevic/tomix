using System.Text.Json;
using Tomix.Cli.Output;
using Tomix.Core.Diagnostics;

namespace Tomix.Cli.Tests;

/// <summary>
/// Pins the production JSON error envelope (docs/error-codes.md) by exercising
/// <see cref="ErrorOutput"/> itself rather than a re-implementation of its serializer.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class ErrorOutputContractTests
{
    [Fact]
    public void JsonEnvelope_HasAllFourFields()
    {
        var json = CaptureJson(new TomixDiagnostic(
            "TOMIX_TEST", DiagnosticSeverity.Error, "test message", "test hint"));

        Assert.Equal("test message", json.RootElement.GetProperty("error").GetString());
        Assert.Equal("TOMIX_TEST", json.RootElement.GetProperty("code").GetString());
        Assert.Equal("Error", json.RootElement.GetProperty("severity").GetString());
        Assert.Equal("test hint", json.RootElement.GetProperty("hint").GetString());
    }

    [Fact]
    public void JsonEnvelope_NullHint_IsPresentAsJsonNull()
    {
        // The envelope always has all four keys; a missing hint serializes as null
        // ("May be null" per docs/error-codes.md), not as an omitted property.
        var json = CaptureJson(new TomixDiagnostic(
            "TOMIX_TEST", DiagnosticSeverity.Error, "test message"));

        Assert.True(json.RootElement.TryGetProperty("hint", out var hint));
        Assert.Equal(JsonValueKind.Null, hint.ValueKind);
    }

    [Fact]
    public void JsonEnvelope_UsesFirstErrorDiagnostic_NotWarnings()
    {
        var json = CaptureJson(
            new TomixDiagnostic("TOMIX_WARN", DiagnosticSeverity.Warning, "warning first"),
            new TomixDiagnostic("TOMIX_REAL_ERROR", DiagnosticSeverity.Error, "the error"));

        Assert.Equal("TOMIX_REAL_ERROR", json.RootElement.GetProperty("code").GetString());
    }

    private static JsonDocument CaptureJson(params TomixDiagnostic[] diagnostics)
    {
        var original = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            ErrorOutput.Write(diagnostics, "json");
        }
        finally
        {
            Console.SetError(original);
        }

        return JsonDocument.Parse(stderr.ToString());
    }
}
