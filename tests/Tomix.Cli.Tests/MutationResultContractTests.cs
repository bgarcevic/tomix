using System.Text.Json;
using Tomix.App.Add;
using Tomix.App.Format;
using Tomix.App.Mv;
using Tomix.App.Replace;
using Tomix.App.Rm;
using Tomix.App.Script;
using Tomix.App.Set;
using Tomix.Cli.Output;

namespace Tomix.Cli.Tests;

/// <summary>
/// JSON contract tests for mutation command result types.
/// These protect the <c>--output-format json</c> output shape — especially the
/// <c>saved</c> field (type <c>object</c>: null/true/path) and <c>staged</c> field
/// (type <c>bool?</c>).
/// <para>
/// Serialization goes through <see cref="JsonOutput"/>, the same code path the commands use,
/// so that omission driven by <c>[JsonIgnore(WhenWritingNull)]</c> is actually exercised. Do not
/// substitute a locally-built <c>JsonSerializerOptions</c>: options that add a blanket
/// <c>DefaultIgnoreCondition</c> make every "omits" assertion below pass whether or not the
/// attribute is present.
/// </para>
/// <para>
/// The <c>bpa</c> results are deliberately absent: <c>BpaRunResult</c> and
/// <c>BpaRulesIgnoreResult</c> are never serialized directly — <c>BpaRunRenderer.ToJson</c> and
/// <c>BpaRulesRenderer.ToIgnoreJson</c> project them onto anonymous objects that emit
/// <c>saved</c>/<c>staged</c> unconditionally. That contract is pinned in
/// <see cref="BpaJsonContractTests"/>.
/// </para>
/// </summary>
public sealed class MutationResultContractTests
{
    private static string Serialize<T>(T value) => JsonOutput.Serialize(value);

    // ── Format: ObjectFormatResult ──────────────────────────────────────────

    [Fact]
    public void ObjectFormatResult_NotSaved_OmitsSavedAndStaged()
    {
        var result = new ObjectFormatResult(true, "Sales/Total", "dax", "formatted", "CALCULATE()", null);

        Assert.DoesNotContain("\"saved\"", Serialize(result));
        Assert.DoesNotContain("\"staged\"", Serialize(result));
    }

    [Fact]
    public void ObjectFormatResult_SavedTrue_SerializesSavedAsBooleanTrue()
    {
        var result = new ObjectFormatResult(true, "Sales/Total", "dax", "formatted", "CALCULATE()", Saved: true);
        var json = JsonDocument.Parse(Serialize(result));

        Assert.Equal(JsonValueKind.True, json.RootElement.GetProperty("saved").ValueKind);
    }

    [Fact]
    public void ObjectFormatResult_SavedPath_SerializesSavedAsString()
    {
        var result = new ObjectFormatResult(true, "Sales/Total", "dax", "formatted", "CALCULATE()", Saved: "output/model");
        var json = JsonDocument.Parse(Serialize(result));

        Assert.Equal("output/model", json.RootElement.GetProperty("saved").GetString());
    }

    [Fact]
    public void ObjectFormatResult_StagedTrue_SerializesStagedAsBoolean()
    {
        var result = new ObjectFormatResult(true, "Sales/Total", "dax", "formatted", "CALCULATE()", null, Staged: true);
        var json = JsonDocument.Parse(Serialize(result));

        Assert.Equal(JsonValueKind.True, json.RootElement.GetProperty("staged").ValueKind);
    }

    // ── Format: ModelFormatResult ───────────────────────────────────────────

    [Fact]
    public void ModelFormatResult_WithSavedAndStaged_SerializesBoth()
    {
        var result = new ModelFormatResult(3, 2, 1, 0,
            [new ModelFormatObjectResult("Sales", "Sales", "formatted", null)],
            Saved: "out/path", Staged: true);
        var json = JsonDocument.Parse(Serialize(result));

        Assert.Equal("out/path", json.RootElement.GetProperty("saved").GetString());
        Assert.Equal(JsonValueKind.True, json.RootElement.GetProperty("staged").ValueKind);
    }

    // ── Add: AddModelObjectResult ───────────────────────────────────────────

    [Fact]
    public void AddModelObjectResult_SavedIsObject_CanBeBooleanOrString()
    {
        var boolResult = new AddModelObjectResult(new { }, Saved: true, Staged: null);
        var boolJson = JsonDocument.Parse(Serialize(boolResult));
        Assert.Equal(JsonValueKind.True, boolJson.RootElement.GetProperty("saved").ValueKind);

        var strResult = new AddModelObjectResult(new { }, Saved: "custom/path", Staged: null);
        var strJson = JsonDocument.Parse(Serialize(strResult));
        Assert.Equal("custom/path", strJson.RootElement.GetProperty("saved").GetString());
    }

    [Fact]
    public void AddModelObjectResult_Defaults_OmitRevertedAndExistingPath()
    {
        var result = new AddModelObjectResult("Sales/M", Saved: false, Staged: null);
        var json = Serialize(result);

        Assert.DoesNotContain("\"reverted\"", json);
        Assert.DoesNotContain("\"existingPath\"", json);
    }

    [Fact]
    public void MoveAndRemoveResults_OmitRevertedAtDefault_IncludeOnRevert()
    {
        Assert.DoesNotContain("\"reverted\"", Serialize(new MoveModelObjectResult("A", "B", Saved: false, Staged: null)));
        Assert.DoesNotContain("\"reverted\"", Serialize(new RemoveModelObjectResult(false, null, null, null, null)));

        Assert.Contains("\"reverted\": true", Serialize(
            new MoveModelObjectResult("A", "B", Saved: false, Staged: null, Reverted: true)));
        Assert.Contains("\"reverted\": true", Serialize(
            new RemoveModelObjectResult(false, null, null, null, null, Reverted: true)));
    }

    [Fact]
    public void AddModelObjectResult_RevertAndNoOp_IncludeNewFields()
    {
        var reverted = Serialize(new AddModelObjectResult(false, Saved: false, Staged: null, Reverted: true));
        Assert.Contains("\"reverted\": true", reverted);

        var noOp = JsonDocument.Parse(Serialize(
            new AddModelObjectResult(false, Saved: false, Staged: null, ExistingPath: "Sales/M")));
        Assert.Equal("Sales/M", noOp.RootElement.GetProperty("existingPath").GetString());
    }

    // ── Remove: RemoveModelObjectResult ─────────────────────────────────────

    [Fact]
    public void RemoveModelObjectResult_WithoutSave_OmitsSavedAndStaged()
    {
        var result = new RemoveModelObjectResult("Sales/COL", Saved: null, Staged: null, Reason: "unused", Path: null);
        var json = Serialize(result);

        Assert.DoesNotContain("\"saved\"", json);
        Assert.DoesNotContain("\"staged\"", json);
        Assert.Contains("\"reason\"", json);
    }

    // ── Set: SetModelPropertyResult ─────────────────────────────────────────

    [Fact]
    public void SetModelPropertyResult_WithSaveAndStage_SerializesCorrectly()
    {
        var result = new SetModelPropertyResult("Sales[Name]", "description", "updated", Saved: true, 0, Staged: true);
        var json = JsonDocument.Parse(Serialize(result));

        Assert.Equal(JsonValueKind.True, json.RootElement.GetProperty("saved").ValueKind);
        Assert.Equal(JsonValueKind.True, json.RootElement.GetProperty("staged").ValueKind);
    }

    // ── Move: MoveModelObjectResult ─────────────────────────────────────────

    [Fact]
    public void MoveModelObjectResult_SavedAsString_SerializesCorrectly()
    {
        var result = new MoveModelObjectResult("OldName", "NewName", Saved: "out/model", Staged: null);
        var json = JsonDocument.Parse(Serialize(result));

        Assert.Equal("out/model", json.RootElement.GetProperty("saved").GetString());
        Assert.False(json.RootElement.TryGetProperty("staged", out _));
    }

    // ── Replace: ReplaceModelTextResult ─────────────────────────────────────

    [Fact]
    public void ReplaceModelTextResult_WithDryRun_OmitsSavedAndStaged()
    {
        var result = new ReplaceModelTextResult("foo", "bar", DryRun: true, 3, null, null, null);
        var json = Serialize(result);

        Assert.Contains("\"dryRun\": true", json);
        Assert.DoesNotContain("\"saved\"", json);
        Assert.DoesNotContain("\"staged\"", json);
    }

    // ── Script: ScriptRunResult ─────────────────────────────────────────────

    [Fact]
    public void ScriptRunResult_WithSave_SerializesSavedAndStaged()
    {
        var result = ScriptRunResult.Executed(
            modelName: "model",
            durationMs: 100,
            inputs: [],
            messages: [],
            saved: "out/path",
            staged: true);
        var json = JsonDocument.Parse(Serialize(result));

        Assert.Equal("out/path", json.RootElement.GetProperty("saved").GetString());
        Assert.Equal(JsonValueKind.True, json.RootElement.GetProperty("staged").ValueKind);
    }
}
