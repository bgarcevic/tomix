using System.Text.Json;
using Tomix.App.Add;
using Tomix.App.Format;
using Tomix.App.Mutations;
using Tomix.App.Mv;
using Tomix.App.Replace;
using Tomix.App.Rm;
using Tomix.App.Save;
using Tomix.App.Script;
using Tomix.App.Set;
using Tomix.App.Vertipaq;
using Tomix.Cli.Output;

namespace Tomix.Cli.Tests;

/// <summary>
/// JSON contract tests for mutation command results (issue #161). Every mutation result carries
/// the same persistence fields from <see cref="MutationResult"/>: <c>status</c>, <c>dryRun</c>
/// (always a bool), <c>saved</c> (always a bool), <c>savedTo</c>, <c>persistence</c>,
/// <c>target</c>, <c>sync</c> ({status, target?, warning?}) and <c>newValidationErrors</c>. The
/// object path uses a past-tense key (<c>added</c>, <c>moved</c>, <c>removed</c>, <c>set</c>) only
/// when the edit was saved or staged, and a <c>would*</c> key for previews and dry runs.
/// <para>
/// Serialization goes through <see cref="JsonOutput"/>, the same code path the commands use,
/// so that omission driven by <c>[JsonIgnore(WhenWritingNull)]</c> is actually exercised. Do not
/// substitute a locally-built <c>JsonSerializerOptions</c>: options that add a blanket
/// <c>DefaultIgnoreCondition</c> make every "omits" assertion below pass whether or not the
/// attribute is present.
/// </para>
/// <para>
/// The <c>bpa</c> results are projected onto anonymous objects by their renderers; that contract
/// is pinned in <see cref="BpaJsonContractTests"/>.
/// </para>
/// </summary>
public sealed class MutationResultContractTests
{
    private static JsonElement Json<T>(T value) => JsonDocument.Parse(JsonOutput.Serialize(value)).RootElement;

    private static readonly MutationOutcome SavedToFile = new(
        MutationStatus.Saved, "C:/model/def", PersistenceKind.File, SyncOutcome.NotConfigured);

    private static readonly MutationOutcome SavedToDesktop = new(
        MutationStatus.Saved, "localhost:51234 / 0f1e2d3c", PersistenceKind.LiveModel, SyncOutcome.NotConfigured,
        new MutationTarget("localhost:51234", "0f1e2d3c", "Sales Report"));

    public static TheoryData<string, MutationOutcome> Modes => new()
    {
        { "preview", MutationOutcome.Preview },
        { "dryRun", MutationOutcome.DryRun },
        { "unchanged", MutationOutcome.Unchanged },
        { "staged", MutationOutcome.Staged },
        { "saved", SavedToFile },
        { "reverted", MutationOutcome.Reverted },
    };

    /// <summary>Every mutation result type, built with the given outcome.</summary>
    private static IEnumerable<(string Name, MutationResult Result)> AllResults(MutationOutcome outcome)
    {
        yield return ("add", new AddModelObjectResult("Sales/M") { Outcome = outcome });
        yield return ("mv", new MoveModelObjectResult("Sales/A", "Sales/B") { Outcome = outcome });
        yield return ("rm", new RemoveModelObjectResult("Sales/M") { Outcome = outcome });
        yield return ("set", new SetModelPropertyResult("Sales/M", "description", "x", 0) { Outcome = outcome });
        yield return ("replace", new ReplaceModelTextResult("a", "b", 1, null) { Outcome = outcome });
        yield return ("format", new ObjectFormatResult(true, "Sales/M", "DAX", "formatted", "1") { Outcome = outcome });
        yield return ("format-model", new ModelFormatResult(1, 1, 0, 0, []) { Outcome = outcome });
        yield return ("save", new SaveModelResult("tmdl") { Outcome = outcome });
        yield return ("script", ScriptRunResult.Executed("model", 1, [], [], outcome));
        yield return ("vertipaq", new VertipaqAnnotateResult(1, 0) { Outcome = outcome });
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void EveryResult_EmitsStableTypesForSharedFields(string status, MutationOutcome outcome)
    {
        foreach (var (name, result) in AllResults(outcome))
        {
            var json = Json<object>(result);

            Assert.True(json.GetProperty("status").GetString() == status, $"{name}: status");
            Assert.True(json.GetProperty("saved").ValueKind is JsonValueKind.True or JsonValueKind.False, $"{name}: saved is bool");
            Assert.True(json.GetProperty("dryRun").ValueKind is JsonValueKind.True or JsonValueKind.False, $"{name}: dryRun is bool");
            Assert.Equal(JsonValueKind.Object, json.GetProperty("sync").ValueKind);
            Assert.False(json.TryGetProperty("synced", out _), $"{name}: legacy synced");
            Assert.False(json.TryGetProperty("staged", out _), $"{name}: legacy staged");
            Assert.False(json.TryGetProperty("reverted", out _), $"{name}: legacy reverted");
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void SavedAndSavedTo_AreSetOnlyWhenSaved(string status, MutationOutcome outcome)
    {
        var json = Json(new AddModelObjectResult("Sales/M") { Outcome = outcome });

        Assert.Equal(status == "saved", json.GetProperty("saved").GetBoolean());
        Assert.Equal(status == "saved", json.TryGetProperty("savedTo", out _));
        Assert.Equal(status == "saved", json.TryGetProperty("persistence", out _));
        Assert.Equal(status == "dryRun", json.GetProperty("dryRun").GetBoolean());
    }

    [Theory]
    [InlineData("saved", "added", null)]
    [InlineData("staged", "added", null)]
    [InlineData("preview", null, "wouldAdd")]
    [InlineData("dryRun", null, "wouldAdd")]
    [InlineData("unchanged", null, null)]
    [InlineData("reverted", null, null)]
    public void ObjectKey_IsPastTenseOnlyWhenApplied(string status, string? appliedKey, string? previewKey)
    {
        var outcome = Modes.Single(row => (string)row[0] == status)[1] as MutationOutcome;
        var json = Json(new AddModelObjectResult("Sales/M") { Outcome = outcome! });

        Assert.Equal(appliedKey is not null, json.TryGetProperty("added", out _));
        Assert.Equal(previewKey is not null, json.TryGetProperty("wouldAdd", out _));
    }

    [Fact]
    public void DryRunKeys_NeverUsePastTense()
    {
        var keys = AllResults(MutationOutcome.DryRun)
            .SelectMany(r => Json<object>(r.Result).EnumerateObject().Select(p => p.Name))
            .ToHashSet();

        Assert.DoesNotContain("added", keys);
        Assert.DoesNotContain("moved", keys);
        Assert.DoesNotContain("removed", keys);
        Assert.DoesNotContain("set", keys);
        Assert.Contains("wouldAdd", keys);
        Assert.Contains("wouldMove", keys);
        Assert.Contains("wouldRemove", keys);
        Assert.Contains("wouldSet", keys);
    }

    [Fact]
    public void LiveModelSave_ReportsPersistenceBoundaryAndTarget()
    {
        var json = Json(new RemoveModelObjectResult("Sales/M") { Outcome = SavedToDesktop });

        Assert.Equal("Sales/M", json.GetProperty("removed").GetString());
        Assert.True(json.GetProperty("saved").GetBoolean());
        Assert.Equal("localhost:51234 / 0f1e2d3c", json.GetProperty("savedTo").GetString());
        Assert.Equal("liveModel", json.GetProperty("persistence").GetString());
        var target = json.GetProperty("target");
        Assert.Equal("localhost:51234", target.GetProperty("server").GetString());
        Assert.Equal("0f1e2d3c", target.GetProperty("database").GetString());
        Assert.Equal("Sales Report", target.GetProperty("model").GetString());
    }

    [Theory]
    [InlineData(SyncStatus.NotAttempted, "notAttempted")]
    [InlineData(SyncStatus.NotConfigured, "notConfigured")]
    [InlineData(SyncStatus.Skipped, "skipped")]
    [InlineData(SyncStatus.Succeeded, "succeeded")]
    [InlineData(SyncStatus.Failed, "failed")]
    public void Sync_IsAStatusObject(SyncStatus status, string expected)
    {
        var sync = new SyncOutcome(status, status == SyncStatus.Succeeded ? "ws / Model" : null);
        var json = Json(new AddModelObjectResult("Sales/M") { Outcome = SavedToFile with { Sync = sync } });

        var element = json.GetProperty("sync");
        Assert.Equal(expected, element.GetProperty("status").GetString());
        Assert.Equal(status == SyncStatus.Succeeded, element.TryGetProperty("target", out _));
        Assert.False(element.TryGetProperty("warning", out _));
    }

    [Fact]
    public void Sync_DefaultsToNotAttempted_WhenNothingWasSaved()
    {
        var json = Json(new AddModelObjectResult("Sales/M") { Outcome = MutationOutcome.Preview });

        Assert.Equal("notAttempted", json.GetProperty("sync").GetProperty("status").GetString());
    }

    [Fact]
    public void RemoveModelObjectResult_GuardedDryRun_SerializesWouldRemoveWithBlockers()
    {
        var json = Json(new RemoveModelObjectResult(
            "Sales/Amount", Reason: "would_block", BrokenReferences: ["Sales/Total Sales"])
        { Outcome = MutationOutcome.DryRun });

        Assert.True(json.GetProperty("dryRun").GetBoolean());
        Assert.Equal("Sales/Amount", json.GetProperty("wouldRemove").GetString());
        Assert.Equal("would_block", json.GetProperty("reason").GetString());
        Assert.Equal(1, json.GetProperty("brokenReferences").GetArrayLength());
        Assert.False(json.GetProperty("saved").GetBoolean());
    }

    [Fact]
    public void RemoveModelObjectResult_UnchangedDryRun_KeepsDryRunTrue()
    {
        var outcome = MutationOutcome.Unchanged with { DryRunRequested = true };
        var json = Json(new RemoveModelObjectResult("Sales/Nope", Reason: "not_found") { Outcome = outcome });

        Assert.Equal("unchanged", json.GetProperty("status").GetString());
        Assert.True(json.GetProperty("dryRun").GetBoolean());
        Assert.Equal("Sales/Nope", json.GetProperty("path").GetString());
    }

    [Fact]
    public void AddModelObjectResult_NoOp_ReportsExistingPath()
    {
        var json = Json(new AddModelObjectResult(null, ExistingPath: "Sales/M") { Outcome = MutationOutcome.Unchanged });

        Assert.Equal("Sales/M", json.GetProperty("existingPath").GetString());
        Assert.False(json.TryGetProperty("added", out _));
    }

    [Fact]
    public void SetModelPropertyResult_Revert_OmitsValidationErrors()
    {
        // --revert never opens the model, so there is no measurement: the field is omitted
        // rather than lying with a 0.
        var json = Json(new SetModelPropertyResult("Sales", Property: null, Value: null, ValidationErrors: null)
        { Outcome = MutationOutcome.Reverted });

        Assert.False(json.TryGetProperty("validationErrors", out _));
        Assert.False(json.TryGetProperty("property", out _));
        Assert.False(json.TryGetProperty("value", out _));
        Assert.Equal("Sales", json.GetProperty("path").GetString());
        Assert.Equal("reverted", json.GetProperty("status").GetString());
    }

    [Fact]
    public void ObjectFormatResult_ReportsFormatStatusSeparatelyFromMutationStatus()
    {
        var json = Json(new ObjectFormatResult(true, "Sales/M", "DAX", "formatted", "1") { Outcome = MutationOutcome.Staged });

        Assert.Equal("formatted", json.GetProperty("formatStatus").GetString());
        Assert.Equal("staged", json.GetProperty("status").GetString());
    }

    [Fact]
    public void ReplaceModelTextResult_Preview_ReportsPreviewsWithoutSaving()
    {
        var json = Json(new ReplaceModelTextResult("foo", "bar", 3, []) { Outcome = MutationOutcome.DryRun });

        Assert.True(json.GetProperty("dryRun").GetBoolean());
        Assert.False(json.GetProperty("saved").GetBoolean());
        Assert.Equal(JsonValueKind.Array, json.GetProperty("previews").ValueKind);
    }
}
