using System.CommandLine;
using System.Reflection;
using System.Text.Json;
using Tomix.Cli.Commands;
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

    [Theory]
    // Explicit --error-format always wins, whatever stdout is doing.
    [InlineData("ls --error-format json", "json")]
    [InlineData("ls --error-format json --output-format text", "json")]
    [InlineData("ls --error-format text --output-format json", "text")]
    // No explicit value: JSON stdout implies JSON errors, so a script asking for JSON never has
    // to parse a colored text error off stderr (docs/error-codes.md).
    [InlineData("ls --output-format json", "json")]
    // Anything else stays text — csv and the model formats are not error-shaped.
    [InlineData("ls", null)]
    [InlineData("ls --output-format csv", null)]
    [InlineData("get X --output-format tmdl", null)]
    public void ErrorFormatValue_DerivesFromOutputFormat(string commandLine, string? expected)
    {
        var parseResult = TestRoot.Full().Parse(commandLine);
        var outputFormat = GlobalOptions.OutputFormatValue(parseResult);

        Assert.Equal(expected, GlobalOptions.ErrorFormatValue(parseResult, outputFormat));
    }

    /// <summary>
    /// The parameterless overload — used by the top-level crash handlers, the unknown-option guard,
    /// and the shared <c>--recent</c> helper, all of which run before a command resolves its own
    /// format — has to see a <c>--output-format</c> bound to a command's <em>local</em> option.
    /// </summary>
    /// <remarks>
    /// <c>doctor</c>, <c>update</c> and <c>completion</c> declare their own <c>--output-format</c>
    /// next to the recursive global one. Reading the global option alone reports its default for
    /// exactly those three, so a caller who asked for JSON got a text error off stderr — the gap
    /// the rest of this file exists to close, left open on the paths that cannot ask a command.
    /// </remarks>
    [Theory]
    [InlineData("doctor --output-format json", OutputFormats.Json)]
    [InlineData("update --check --output-format json", OutputFormats.Json)]
    [InlineData("completion bash --output-format json", OutputFormats.Json)]
    [InlineData("ls --output-format json", OutputFormats.Json)]
    [InlineData("doctor", null)]
    [InlineData("doctor --output-format text", null)]
    public void ErrorFormatValue_SeesAFormatBoundToALocalOption(string commandLine, string? expected)
        => Assert.Equal(expected, GlobalOptions.ErrorFormatValue(TestRoot.Full().Parse(commandLine)));

    /// <summary>
    /// Eleven command modules once ignored <c>--error-format json</c> — including <c>bpa</c>,
    /// <c>config</c>, <c>session</c>, <c>stage</c>, and <c>validate</c> — because
    /// <see cref="CommandOutput.Render{T}(ParseResult, Tomix.Core.Results.TomixResult{T}, string, Action{T})"/>
    /// had sibling overloads whose <c>errorFormat</c> silently defaulted to <c>null</c>. Nothing
    /// failed: the text error still reached stderr, so no test noticed and the whole suite stayed
    /// green. Rather than assert the behaviour command by command (which the next command would
    /// not be added to), this pins the shape that made forgetting impossible — every public
    /// <c>Render</c> overload must either take a <see cref="ParseResult"/> and derive the stderr
    /// format, or demand an explicit non-optional one.
    /// </summary>
    [Fact]
    public void EveryRenderOverload_ForcesAnErrorFormatDecision()
    {
        var lax = typeof(CommandOutput)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == nameof(CommandOutput.Render))
            .Where(m => !m.GetParameters().Any(p => p.ParameterType == typeof(ParseResult)))
            .Where(m => !m.GetParameters().Any(p => p.Name == "errorFormat" && !p.IsOptional))
            .Select(m => m.ToString()!)
            .ToList();

        Assert.True(lax.Count == 0,
            "CommandOutput.Render overloads that let a caller omit the stderr format — any command " +
            "using one will silently ignore --error-format json:" +
            Environment.NewLine + string.Join(Environment.NewLine, lax));
    }

    private static JsonDocument CaptureJson(params TomixDiagnostic[] diagnostics)
    {
        var captured = ConsoleCapture.Run(() => ErrorOutput.Write(diagnostics, "json"));

        return JsonDocument.Parse(captured.Stderr);
    }
}
