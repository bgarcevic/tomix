using Tomix.App.Script;
using Tomix.App.Tests.Support;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;

namespace Tomix.App.Tests;

/// <summary>
/// Behavioral tests for <see cref="ScriptHandler.HandleAsync"/> and the internal
/// <c>ScriptExpressionEvaluator</c>. Pipeline tests run the real TMDL provider against a
/// throwaway copy of <c>samples/basic-tmdl</c> (3 tables, 2 relationships, 0 roles) with a
/// temp-dir staging store, so nothing touches the developer's real <c>~/.tomix</c>.
/// </summary>
public sealed class ScriptHandlerTests : IDisposable
{
    private readonly TempConfigDir _config = new();
    private readonly string _model = CopySample();

    public void Dispose()
    {
        _config.Dispose();
        if (Directory.Exists(_model))
            Directory.Delete(_model, recursive: true);
    }

    // ---- Input resolution failures -------------------------------------------------------

    [Fact]
    public async Task HandleAsync_MissingScriptFile_FailsWithScriptFileNotFound()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"tomix-no-such-script-{Guid.NewGuid():N}.csx");

        var result = await NewHandler().HandleAsync(
            NewRequest(files: [missing]), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_SCRIPT_FILE_NOT_FOUND", result.Diagnostics[0].Code);
        Assert.Contains(missing, result.Diagnostics[0].Message);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_NoInputs_FailsWithScriptRequired()
    {
        // Whitespace-only expressions are dropped during input resolution.
        var result = await NewHandler().HandleAsync(
            NewRequest(expressions: ["   "]), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_SCRIPT_REQUIRED", result.Diagnostics[0].Code);
        Assert.Equal(1, result.ExitCode);
    }

    // ---- Pre-flight (MutationLifecycle) failures -----------------------------------------

    [Fact]
    public async Task HandleAsync_SaveCombinedWithStage_FailsWithStageSaveConflict()
    {
        var result = await NewHandler().HandleAsync(
            NewRequest(expressions: ["1 + 1"], save: true, stage: true), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_STAGE_SAVE_CONFLICT", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_NoProviderClaimsModel_FailsWithNoProvider()
    {
        var missingModel = Path.Combine(Path.GetTempPath(), $"tomix-no-such-model-{Guid.NewGuid():N}");
        var handler = new ScriptHandler([], _config.Stores);

        var result = await handler.HandleAsync(
            NewRequest(model: missingModel, expressions: ["1 + 1"]), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_NO_PROVIDER", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    // ---- Dry run -------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_DryRunWithValidScripts_ReportsSuccessWithoutExecuting()
    {
        var result = await NewHandler().HandleAsync(
            NewRequest(expressions: ["Model.Tables.Count", "2 + 3"], dryRun: true),
            CancellationToken.None);

        Assert.True(result.Success);
        var data = result.Data!;
        Assert.True(data.DryRun);
        Assert.True(data.Success);
        Assert.Equal(0, data.ScriptsExecuted);
        Assert.Empty(data.Messages);
        Assert.Equal(2, data.Scripts.Count);
        Assert.All(data.Scripts, script => Assert.True(script.Success));
        Assert.All(data.Scripts, script => Assert.Empty(script.Errors));
    }

    [Fact]
    public async Task HandleAsync_DryRunWithCompileError_ReportsPerScriptErrors()
    {
        var result = await NewHandler().HandleAsync(
            NewRequest(expressions: ["Model.Tables.Count", "Foo.Bar()"], dryRun: true),
            CancellationToken.None);

        Assert.True(result.Success);
        var data = result.Data!;
        Assert.True(data.DryRun);
        Assert.False(data.Success);
        Assert.True(data.Scripts[0].Success);
        Assert.False(data.Scripts[1].Success);
        Assert.Contains("Unsupported script expression", data.Scripts[1].Errors[0]);
    }

    // ---- Successful execution ------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_MultipleValidExpressions_ExecutesAllInOrder()
    {
        var result = await NewHandler().HandleAsync(
            NewRequest(expressions: ["Model.Tables.Count", "Model.Relationships.Count", "2 + 3"]),
            CancellationToken.None);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var data = result.Data!;
        Assert.True(data.Success);
        Assert.False(data.DryRun);
        Assert.Equal(3, data.ScriptsExecuted);
        Assert.Equal(["3", "2", "5"], data.Messages.Select(m => m.Text));
        Assert.All(data.Messages, message => Assert.Equal("output", message.Level));
        Assert.False((bool)data.Saved);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_ScriptFileInput_ReadsAndExecutesFile()
    {
        // The script lives outside the model folder so the TMDL provider never sees it.
        var scriptPath = Path.Combine(Path.GetTempPath(), $"tomix-script-{Guid.NewGuid():N}.csx");
        File.WriteAllText(scriptPath, "return Model.Tables.Count;");
        try
        {
            var result = await NewHandler().HandleAsync(
                NewRequest(files: [scriptPath]), CancellationToken.None);

            Assert.True(result.Success);
            var data = result.Data!;
            Assert.Equal(1, data.ScriptsExecuted);
            Assert.Equal(scriptPath, data.Inputs[0].Source);
            Assert.Equal("3", Assert.Single(data.Messages).Text);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task HandleAsync_Revert_ReturnsEmptySuccessResult()
    {
        var result = await NewHandler().HandleAsync(
            NewRequest(expressions: ["1 + 1"], revert: true), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("", result.Data!.ModelName);
        Assert.Equal(0, result.Data.ScriptsExecuted);
    }

    // ---- Execution failures --------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_CompileErrorMidRun_ReturnsFailedResultWithScriptIndex()
    {
        var result = await NewHandler().HandleAsync(
            NewRequest(expressions: ["1 + 1", "Bogus()"]), CancellationToken.None);

        // The handler reports script failures as a successful TomixResult carrying a
        // failed payload and a non-zero exit code.
        Assert.True(result.Success);
        Assert.Equal(1, result.ExitCode);
        var data = result.Data!;
        Assert.False(data.Success);
        Assert.Equal("<inline>", data.FailedScript);
        Assert.Equal(2, data.ScriptIndex);
        Assert.Equal(1, data.ScriptsExecuted);
        Assert.Contains("Unsupported script expression", Assert.Single(data.CompileErrors));
        Assert.Null(data.RuntimeError);
        // Output produced before the failing script is preserved.
        Assert.Equal("2", Assert.Single(data.Messages).Text);
    }

    [Fact]
    public async Task HandleAsync_RuntimeErrorBadIndex_ReturnsRuntimeErrorMessage()
    {
        var result = await NewHandler().HandleAsync(
            NewRequest(expressions: ["Model.Tables[99].Name"]), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, result.ExitCode);
        var data = result.Data!;
        Assert.False(data.Success);
        Assert.Empty(data.CompileErrors);
        // Pins the current out-of-range message verbatim (it deliberately mimics
        // ArgumentOutOfRangeException formatting, including the CRLF).
        Assert.Equal(" (Parameter 'index')\r\nActual value was 99.", data.RuntimeError);
    }

    // ---- ScriptExpressionEvaluator: compile checks ----------------------------------------

    [Theory]
    [InlineData("Model.Tables.Count")]
    [InlineData("return Model.Name;")]
    [InlineData("Info(Model.Relationships.Count)")]
    [InlineData("Model.Tables[0].Name.ToString()")]
    [InlineData("6 / 4")]
    public void CanCompile_SupportedExpression_ReturnsTrue(string code)
    {
        Assert.True(ScriptExpressionEvaluator.CanCompile(code, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void CanCompile_UnknownFunction_ReturnsFalseWithUnsupportedError()
    {
        Assert.False(ScriptExpressionEvaluator.CanCompile("DoMagic(Model)", out var error));
        Assert.Equal("error: Unsupported script expression: DoMagic(Model)", error);
    }

    [Fact]
    public void CanCompile_EmptyExpression_ReturnsFalseWithEmptyError()
    {
        Assert.False(ScriptExpressionEvaluator.CanCompile("   ", out var error));
        Assert.Equal("error: Script expression is empty.", error);
    }

    // ---- ScriptExpressionEvaluator: evaluation --------------------------------------------

    [Theory]
    [InlineData("Model.Name", "Contoso")]
    [InlineData("Model.Tables.Count", "2")]
    [InlineData("Model.Relationships.Count", "1")]
    [InlineData("Model.Roles.Count", "0")]
    [InlineData("Model.Tables[0].Name", "Sales")]
    [InlineData("Model.Tables[1].Name", "Customers")]
    [InlineData("Model.Tables[0].Columns.Count", "2")]
    [InlineData("Model.Tables[0].Measures.Count", "1")]
    [InlineData("Model.Tables[0].Measures[0].Name", "Total")]
    [InlineData("Model.Tables[0].Columns[1].Name", "Amount")]
    [InlineData("2 + 3", "5")]
    [InlineData("6 / 4", "1.5")]
    [InlineData("6 / 3", "2")]
    [InlineData("-2 * 3", "-6")]
    public void TryEvaluate_SupportedExpression_ProducesOutputMessage(string code, string expected)
    {
        var ok = ScriptExpressionEvaluator.TryEvaluate(
            Snapshot(), Summary(), code, out var result, out var compileError, out var runtimeError);

        Assert.True(ok);
        Assert.Null(compileError);
        Assert.Null(runtimeError);
        Assert.NotNull(result);
        Assert.Equal("output", result.Level);
        Assert.Equal(expected, result.Text);
    }

    [Fact]
    public void TryEvaluate_UnnamedModel_ReturnsModelAsName()
    {
        var ok = ScriptExpressionEvaluator.TryEvaluate(
            Snapshot(), Summary() with { Name = "(unnamed)" }, "Model.Name",
            out var result, out _, out _);

        Assert.True(ok);
        Assert.Equal("Model", result!.Text);
    }

    [Fact]
    public void TryEvaluate_UnknownFunction_ReturnsCompileError()
    {
        var ok = ScriptExpressionEvaluator.TryEvaluate(
            Snapshot(), Summary(), "DoMagic(Model)", out var result, out var compileError, out var runtimeError);

        Assert.False(ok);
        Assert.Null(result);
        Assert.Null(runtimeError);
        Assert.Equal("error: Unsupported script expression: DoMagic(Model)", compileError);
    }

    [Fact]
    public void TryEvaluate_TableIndexOutOfRange_ReturnsRuntimeError()
    {
        var ok = ScriptExpressionEvaluator.TryEvaluate(
            Snapshot(), Summary(), "Model.Tables[5].Name", out var result, out var compileError, out var runtimeError);

        Assert.False(ok);
        Assert.Null(result);
        Assert.Null(compileError);
        // Current behavior as-is: the message mimics ArgumentOutOfRangeException formatting.
        Assert.Equal(" (Parameter 'index')\r\nActual value was 5.", runtimeError);
    }

    [Fact]
    public void TryEvaluate_ChildIndexOutOfRange_ReturnsRuntimeError()
    {
        var ok = ScriptExpressionEvaluator.TryEvaluate(
            Snapshot(), Summary(), "Model.Tables[0].Measures[7].Name", out _, out var compileError, out var runtimeError);

        Assert.False(ok);
        Assert.Null(compileError);
        Assert.Equal(" (Parameter 'index')\r\nActual value was 7.", runtimeError);
    }

    [Fact]
    public void TryEvaluate_DivideByZero_ReturnsRuntimeError()
    {
        var ok = ScriptExpressionEvaluator.TryEvaluate(
            Snapshot(), Summary(), "1 / 0", out _, out var compileError, out var runtimeError);

        Assert.False(ok);
        Assert.Null(compileError);
        Assert.Equal("Attempted to divide by zero.", runtimeError);
    }

    // ---- Helpers ---------------------------------------------------------------------------

    private ScriptHandler NewHandler() => new([new TmdlModelProvider()], _config.Stores);

    private ScriptRunRequest NewRequest(
        string? model = null,
        IReadOnlyList<string>? files = null,
        IReadOnlyList<string>? expressions = null,
        bool dryRun = false,
        bool save = false,
        bool stage = false,
        bool revert = false)
        => new(
            new ModelReference(model ?? _model),
            files ?? [],
            expressions ?? [],
            DryRun: dryRun,
            Force: false,
            Save: save,
            SaveTo: null,
            Serialization: null,
            Stage: stage,
            Revert: revert,
            NoSync: true);

    private static ModelSummary Summary()
        => new("Contoso", 1601, Tables: 2, Columns: 3, Measures: 1, Relationships: 1, Roles: 0);

    private static ModelSnapshot Snapshot()
        => new("Contoso", 1601,
        [
            Table("Sales",
                Child("SaleID", ModelObjectKind.Column),
                Child("Amount", ModelObjectKind.Column),
                Child("Total", ModelObjectKind.Measure),
                Child("Sales-partition", ModelObjectKind.Partition)),
            Table("Customers",
                Child("CustomerID", ModelObjectKind.Column))
        ]);

    private static ModelObject Table(string name, params ModelObject[] children)
        => new(name, ModelObjectKind.Table, name, null, null, null, false, null, children);

    private static ModelObject Child(string name, ModelObjectKind kind)
        => new(name, kind, name, null, null, null, false, null, []);

    private static string CopySample()
    {
        var dest = Path.Combine(Path.GetTempPath(), $"tomix-script-test-{Guid.NewGuid():N}");
        CopyDirectory(LocateSample(), dest);
        return dest;
    }

    private static string LocateSample()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "samples", "basic-tmdl");
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new InvalidOperationException("samples/basic-tmdl not found above test base directory.");
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }
}
