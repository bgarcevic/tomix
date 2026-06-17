using System.Text.Json;
using System.Text.Json.Serialization;
using Tomix.App.Add;
using Tomix.App.Bpa;
using Tomix.App.Format;
using Tomix.App.Mv;
using Tomix.App.Replace;
using Tomix.App.Rm;
using Tomix.App.Script;
using Tomix.App.Set;

namespace Tomix.App.Tests;

/// <summary>
/// JSON contract tests for mutation command result types.
/// These protect the <c>--output-format json</c> output shape — especially the
/// <c>saved</c> field (type <c>object</c>: null/true/path) and <c>staged</c> field
/// (type <c>bool?</c>).
/// </summary>
public sealed class MutationResultContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    static MutationResultContractTests()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

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

    // ── BPA: BpaRunResult ───────────────────────────────────────────────────

    [Fact]
    public void BpaRunResult_NotSaved_OmitsSavedAndStaged()
    {
        var result = new BpaRunResult([], "model", 5);
        var json = Serialize(result);

        Assert.DoesNotContain("\"saved\"", json);
        Assert.DoesNotContain("\"staged\"", json);
    }

    [Fact]
    public void BpaRunResult_SavedTrue_SerializesSavedAsBoolean()
    {
        var result = new BpaRunResult([], "model", 5, Saved: true);
        var json = JsonDocument.Parse(Serialize(result));

        Assert.Equal(JsonValueKind.True, json.RootElement.GetProperty("saved").ValueKind);
    }

    [Fact]
    public void BpaRunResult_StagedTrue_SerializesStagedAsBoolean()
    {
        var result = new BpaRunResult([], "model", 5, Staged: true);
        var json = JsonDocument.Parse(Serialize(result));

        Assert.Equal(JsonValueKind.True, json.RootElement.GetProperty("staged").ValueKind);
    }

    // ── BPA: BpaRulesIgnoreResult ───────────────────────────────────────────

    [Fact]
    public void BpaRulesIgnoreResult_SavedObject_SerializesCorrectly()
    {
        var result = new BpaRulesIgnoreResult("RULE_1", true, true, ["RULE_1"], Saved: true, Staged: null, "model");
        var json = JsonDocument.Parse(Serialize(result));

        Assert.Equal(JsonValueKind.True, json.RootElement.GetProperty("saved").ValueKind);
        Assert.False(json.RootElement.TryGetProperty("staged", out _));
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
