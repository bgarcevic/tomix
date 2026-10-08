using System.Text.Json;
using Tomix.App.Format.M;

namespace Tomix.App.Tests;

/// <summary>
/// Runs the embedded M engine under Jint, the way <c>tx</c> does. The format and diagnose cases
/// mirror <c>engines/powerquery/smoke.mjs</c>, so a regenerated bundle that works in Node but hits a
/// Jint incompatibility (see #194) fails <c>dotnet test</c>, not only the npm smoke.
/// </summary>
public sealed class PowerQueryEngineTests(PowerQueryEngineTests.SharedEngine shared)
    : IClassFixture<PowerQueryEngineTests.SharedEngine>
{
    // Shape of a real partition expression from samples/AdventureWorks Sales (Category table).
    private const string CategoryPartition = """
        let
            Source = Table.FromRows(Json.Document(Binary.Decompress(Binary.FromText("i45W", BinaryEncoding.Base64), Compression.Deflate)), let _t = ((type nullable text) meta [Serialized.Text = true]) in type table [Category = _t, Sorting = _t]),
            #"Changed Type" = Table.TransformColumnTypes(Source,{{"Category", type text}, {"Sorting", Int64.Type}})
        in
            #"Changed Type"
        """;

    private readonly PowerQueryEngine _engine = shared.Engine;

    [Fact]
    public async Task GetVersion_ReportsThePinnedPackages()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(RepoPaths.Combine("engines", "powerquery", "package.json")));
        var dependencies = manifest.RootElement.GetProperty("dependencies");

        var version = await _engine.GetVersionAsync(CancellationToken.None);

        Assert.NotNull(version);
        Assert.Equal(dependencies.GetProperty("@microsoft/powerquery-formatter").GetString(), version.Formatter);
        Assert.Equal(dependencies.GetProperty("@microsoft/powerquery-parser").GetString(), version.Parser);
    }

    [Fact]
    public async Task Format_SimpleLet_RoundTripsWithTrailingNewline()
    {
        var result = await _engine.FormatAsync("let x = 1 in x", Options(40), CancellationToken.None);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal("let\n    x = 1\nin\n    x\n", result.Text);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(120)]
    public async Task Format_SamplePartition_IsIdempotent(int maxWidth)
    {
        var first = await _engine.FormatAsync(CategoryPartition, Options(maxWidth), CancellationToken.None);
        Assert.True(first.Ok, first.Error?.Message);

        var second = await _engine.FormatAsync(first.Text!, Options(maxWidth), CancellationToken.None);

        Assert.True(second.Ok, second.Error?.Message);
        Assert.Equal(first.Text, second.Text);
    }

    [Fact]
    public async Task Format_MaxWidth_ChangesTheLayout()
    {
        var narrow = await _engine.FormatAsync(CategoryPartition, Options(40), CancellationToken.None);
        var wide = await _engine.FormatAsync(CategoryPartition, Options(120), CancellationToken.None);

        Assert.True(narrow.Ok && wide.Ok);
        Assert.True(
            narrow.Text!.Split('\n').Length > wide.Text!.Split('\n').Length,
            "A narrower width should wrap onto more lines.");
    }

    [Fact]
    public async Task Format_BrokenM_ReturnsParseErrorWithPosition()
    {
        var result = await _engine.FormatAsync("let\n    x = 1,\nin\n    x", Options(40), CancellationToken.None);

        Assert.False(result.Ok);
        var error = Assert.IsType<PowerQueryEngineError>(result.Error);
        Assert.Equal(PowerQueryErrorKind.Parse, error.Kind);
        Assert.Equal(3, error.Line);
        Assert.Equal(1, error.Column);
        Assert.NotEmpty(error.Message);
    }

    // Known-broken M and exactly where the engine must point. End positions are the offending
    // token's last character (inclusive); lex errors and running out of input know only a start.
    [Theory]
    [InlineData("let x = \"abc in x", "lex", "unterminatedMultilineToken", 1, 9, null, null)]
    [InlineData("let x = 1 /* oops in x", "lex", "unterminatedMultilineToken", 1, 11, null, null)]
    [InlineData("let x = #\"abc", "lex", "unterminatedMultilineToken", 1, 9, null, null)]
    [InlineData("0x", "lex", "expected", 1, 1, null, null)]
    [InlineData("let\n    x = 1,\nin\n    x", "parse", "expectedCsvContinuation", 3, 1, 3, 2)]
    [InlineData("let x = 1", "parse", "expectedClosingTokenKind", 1, 9, null, null)]
    [InlineData("let\n    x = 1\n    y = 2\nin\n    y", "parse", "expectedClosingTokenKind", 3, 5, 3, 5)]
    [InlineData("[a = ]", "parse", "expectedAnyTokenKind", 1, 6, 1, 6)]
    [InlineData("#table({\"a\"}, {{1}", "parse", "expectedClosingTokenKind", 1, 18, null, null)]
    [InlineData("{1, , 2}", "parse", "expectedAnyTokenKind", 1, 5, 1, 5)]
    [InlineData("1 2", "parse", "unusedTokensRemain", 1, 3, 1, 3)]
    public async Task Diagnose_BrokenM_ReportsStageCodeAndSpan(
        string text,
        string stage,
        string code,
        int line,
        int column,
        int? endLine,
        int? endColumn)
    {
        var result = await _engine.DiagnoseAsync(text, CancellationToken.None);

        var error = Assert.Single(result.Errors);
        Assert.NotEmpty(error.Message);
        Assert.Equal(
            new PowerQueryEngineError(
                Enum.Parse<PowerQueryErrorKind>(stage, ignoreCase: true), code, error.Message, line, column, endLine, endColumn),
            error);
    }

    [Fact]
    public async Task Diagnose_LexErrorLineMap_ReportsTheSpecificMessage()
    {
        // The lexer wraps per-line errors in a map whose own message is only "Error on line(s): 0".
        var result = await _engine.DiagnoseAsync("0x", CancellationToken.None);

        Assert.Equal("Expected a hex literal", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task Diagnose_ValidM_ReturnsNoErrors()
    {
        var result = await _engine.DiagnoseAsync("let x = 1 in x", CancellationToken.None);

        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData(25)]
    [InlineData(200)] // Beyond what a default 1 MB thread stack allows under Jint.
    public async Task Format_DeepButRealisticNesting_Formats(int depth)
    {
        var nested = new string('{', depth) + "1" + new string('}', depth);

        var result = await _engine.FormatAsync(nested, Options(40), CancellationToken.None);

        Assert.True(result.Ok, result.Error?.Message);
    }

    [Fact]
    public async Task Format_PathologicalNesting_FailsInsteadOfOverflowingTheStack()
    {
        var nested = new string('{', 5000) + "1" + new string('}', 5000);

        var result = await _engine.FormatAsync(nested, Options(40), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(PowerQueryErrorKind.Internal, result.Error!.Kind);

        var next = await _engine.FormatAsync("let x = 1 in x", Options(40), CancellationToken.None);
        Assert.True(next.Ok, next.Error?.Message);
    }

    [Fact]
    public async Task Format_PreCancelledToken_Throws()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _engine.FormatAsync("let x = 1 in x", Options(40), new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task Format_Timeout_ReturnsInternalFailure()
    {
        // Evaluating the bundle alone takes far longer than this budget.
        using var engine = new PowerQueryEngine(TimeSpan.FromMilliseconds(1));

        var result = await engine.FormatAsync("let x = 1 in x", Options(40), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(PowerQueryErrorKind.Internal, result.Error!.Kind);
        Assert.Contains("timed out", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Format_CancelledMidCall_RethrowsAndTheEngineRecovers()
    {
        using var cts = new CancellationTokenSource();
        // Cancellation lands while the bundle is being evaluated: the loader cancels just before
        // returning it, so the outcome does not depend on how fast the runner is.
        using var engine = new PowerQueryEngine(() =>
        {
            cts.Cancel();
            return PowerQueryEngineBundle.Load();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.FormatAsync("let x = 1 in x", Options(40), cts.Token));

        var result = await engine.FormatAsync("let x = 1 in x", Options(40), CancellationToken.None);
        Assert.True(result.Ok, result.Error?.Message);
    }

    [Fact]
    public async Task Format_BundleFailsToLoad_ReturnsInternalFailure()
    {
        using var engine = new PowerQueryEngine(() => "this is not javascript (");

        var result = await engine.FormatAsync("let x = 1 in x", Options(40), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(PowerQueryErrorKind.Internal, result.Error!.Kind);
        Assert.StartsWith("The offline Power Query engine failed to load:", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Format_BundleWithoutEngineGlobal_ReturnsInternalFailure()
    {
        using var engine = new PowerQueryEngine(() => "globalThis.Other = {};");

        var result = await engine.FormatAsync("let x = 1 in x", Options(40), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("globalThis.TomixM", result.Error!.Message, StringComparison.Ordinal);
    }

    private static PowerQueryFormatOptions Options(int maxWidth) => new("    ", "\n", maxWidth);

    /// <summary>One engine for the class, so the bundle is evaluated once.</summary>
    public sealed class SharedEngine : IDisposable
    {
        internal PowerQueryEngine Engine { get; } = new();

        public void Dispose() => Engine.Dispose();
    }
}
