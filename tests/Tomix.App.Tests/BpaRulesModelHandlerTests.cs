using System.Text.Json.Nodes;
using Tomix.App.Bpa;
using Tomix.App.Mutations;
using Tomix.App.Set;
using Tomix.App.Tests.Support;
using Tomix.Core.Bpa;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;

namespace Tomix.App.Tests;

/// <summary><c>bpa rules add/set/remove</c> against a model's <c>BestPracticeAnalyzer</c> annotation.</summary>
public sealed class BpaRulesModelHandlerTests
{
    private static readonly BpaRuleFields NewRule = new(
        Name: "Measures need a description",
        Scope: "measure",
        Expression: "string.IsNullOrWhitespace(Description)");

    [Fact]
    public async Task RoundTrip_AddSaved_IsListedAsModelRule_AndRunEvaluatesIt()
    {
        using var model = SampleModel.CopyToTemp();
        using var config = new TempConfigDir();

        var added = await HandleAsync(config, model.Path, BpaRulesModelAction.Add, "MY_RULE", NewRule, save: true);

        Assert.True(added.Success, added.Diagnostics.FirstOrDefault()?.Message);
        Assert.Equal(MutationStatus.Saved, added.Data!.Status);
        Assert.Equal(1, added.Data.RuleCount);
        Assert.Equal(BpaRulesFile.ModelSource, added.Data.Rule!.Source);

        var listed = await new BpaRulesListHandler(
                [new TmdlModelProvider()], new BpaUserRuleState(config.Path), httpClient: null, config.Path)
            .HandleAsync(new BpaRulesListRequest(Model: new ModelReference(model.Path), RuleId: "MY_RULE"), CancellationToken.None);
        var info = Assert.Single(listed.Data!.Rules);
        Assert.Equal(BpaRulesFile.ModelSource, info.Source);
        Assert.Equal("Measure", info.Scope);

        var run = await new BpaRunHandler([new TmdlModelProvider()], config.Stores, new BpaUserRuleState(config.Path), config.Path)
            .HandleAsync(new BpaRunRequest(new ModelReference(model.Path)) { RuleIds = ["MY_RULE"] }, CancellationToken.None);
        Assert.True(run.Success, run.Diagnostics.FirstOrDefault()?.Message);
        Assert.Contains(run.Data!.Violations, v => v.RuleId == "MY_RULE" && v.ObjectPath == "Sales/Total Sales");
    }

    [Theory]
    [InlineData(false, MutationStatus.Preview)]
    [InlineData(true, MutationStatus.Staged)]
    public async Task Add_WithoutSave_LeavesTheModelFilesUntouched(bool stage, MutationStatus status)
    {
        using var model = SampleModel.CopyToTemp();
        using var config = new TempConfigDir();

        var result = await HandleAsync(config, model.Path, BpaRulesModelAction.Add, "MY_RULE", NewRule, stage: stage);

        Assert.True(result.Success, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.True(result.Data!.Changed);
        Assert.Equal(status, result.Data.Status);
        Assert.Null(await EmbeddedAnnotationAsync(model.Path));
    }

    [Fact]
    public async Task Set_KeepsFieldsTxDoesNotModel_AndTheirOrder()
    {
        using var model = SampleModel.CopyToTemp();
        using var config = new TempConfigDir();
        await SeedAsync(config, model.Path,
            """[{"ID":"MY_RULE","Tags":["team"],"Name":"n","Category":"c","Severity":2,"Scope":"Table","Expression":"true"}]""");

        var result = await HandleAsync(config, model.Path, BpaRulesModelAction.Set, "my_rule",
            new BpaRuleFields(Severity: "error"), save: true);

        Assert.True(result.Success, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Equal(["Severity"], result.Data!.ChangedFields);
        var rule = (JsonObject)JsonNode.Parse((await EmbeddedAnnotationAsync(model.Path))!)!.AsArray()[0]!;
        Assert.Equal(["ID", "Tags", "Name", "Category", "Severity", "Scope", "Expression"], rule.Select(p => p.Key));
        Assert.Equal("team", rule["Tags"]![0]!.GetValue<string>());
        Assert.Equal(3, rule["Severity"]!.GetValue<int>());
    }

    [Fact]
    public async Task Set_SameValues_ReportsNoChange_AndSavesNothing()
    {
        using var model = SampleModel.CopyToTemp();
        using var config = new TempConfigDir();
        await HandleAsync(config, model.Path, BpaRulesModelAction.Add, "MY_RULE", NewRule, save: true);

        var result = await HandleAsync(config, model.Path, BpaRulesModelAction.Set, "MY_RULE",
            new BpaRuleFields(Name: NewRule.Name), save: true);

        Assert.True(result.Success);
        Assert.False(result.Data!.Changed);
        Assert.Equal(MutationStatus.Unchanged, result.Data.Status);
    }

    [Fact]
    public async Task Remove_LastRule_RemovesTheAnnotation()
    {
        using var model = SampleModel.CopyToTemp();
        using var config = new TempConfigDir();
        await HandleAsync(config, model.Path, BpaRulesModelAction.Add, "MY_RULE", NewRule, save: true);

        var result = await HandleAsync(config, model.Path, BpaRulesModelAction.Remove, "MY_RULE", save: true);

        Assert.True(result.Success, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Equal(0, result.Data!.RuleCount);
        Assert.Null(await EmbeddedAnnotationAsync(model.Path));
    }

    [Fact]
    public async Task Add_ToLegacyKey_MovesItsRulesToTheCorrectKey()
    {
        using var model = SampleModel.CopyToTemp();
        using var config = new TempConfigDir();
        await SeedAsync(config, model.Path,
            """[{"ID":"OLD_RULE","Name":"n","Category":"c","Severity":2,"Scope":"Table","Expression":"true"}]""",
            BpaModelRuleLoader.EmbeddedLegacyKey);

        var result = await HandleAsync(config, model.Path, BpaRulesModelAction.Add, "MY_RULE", NewRule, save: true);

        Assert.True(result.Success, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Equal(2, result.Data!.RuleCount);
        var ids = JsonNode.Parse((await EmbeddedAnnotationAsync(model.Path))!)!.AsArray().Select(r => r!["ID"]!.GetValue<string>());
        Assert.Equal(["OLD_RULE", "MY_RULE"], ids);
        Assert.Null(await EmbeddedAnnotationAsync(model.Path, BpaModelRuleLoader.EmbeddedLegacyKey));
    }

    [Theory]
    // A malformed annotation is reported, never overwritten.
    [InlineData("""{"not":"an array"}""", BpaRulesModelAction.Add, "TOMIX_BPA_RULES_LOAD_FAILED")]
    [InlineData("""[{"ID":"MY_RULE","Name":"n","Scope":"Table","Expression":"true"}]""", BpaRulesModelAction.Add, "TOMIX_BPA_RULE_EXISTS")]
    [InlineData(null, BpaRulesModelAction.Set, "TOMIX_BPA_RULE_NOT_FOUND")]
    [InlineData(null, BpaRulesModelAction.Remove, "TOMIX_BPA_RULE_NOT_FOUND")]
    public async Task Failures_LeaveTheModelUnchanged(string? annotation, BpaRulesModelAction action, string code)
    {
        using var model = SampleModel.CopyToTemp();
        using var config = new TempConfigDir();
        if (annotation is not null)
            await SeedAsync(config, model.Path, annotation);
        var before = await EmbeddedAnnotationAsync(model.Path);

        var result = await HandleAsync(config, model.Path, action, "MY_RULE",
            action == BpaRulesModelAction.Set ? new BpaRuleFields(Severity: "info") : NewRule, save: true);

        Assert.False(result.Success);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(code, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(before, await EmbeddedAnnotationAsync(model.Path));
    }

    [Fact]
    public async Task Add_ChecksFieldsLikeARulesFile()
    {
        using var model = SampleModel.CopyToTemp();
        using var config = new TempConfigDir();

        var result = await HandleAsync(config, model.Path, BpaRulesModelAction.Add, "MY_RULE",
            NewRule with { Scope = "Measure, Widget" }, save: true);

        Assert.False(result.Success);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("TOMIX_BPA_RULE_INVALID_SCOPE", error.Code);
        Assert.Contains("Widget", error.Message);
    }

    private static Task<Tomix.Core.Results.TomixResult<BpaRulesModelResult>> HandleAsync(
        TempConfigDir config, string model, BpaRulesModelAction action, string ruleId,
        BpaRuleFields? fields = null, bool save = false, bool stage = false)
        => new BpaRulesModelHandler([new TmdlModelProvider()], config.Stores).HandleAsync(
            new BpaRulesModelRequest(new ModelReference(model), action, ruleId, fields, Save: save, Stage: stage),
            CancellationToken.None);

    private static async Task SeedAsync(TempConfigDir config, string model, string json, string key = BpaModelRuleLoader.EmbeddedKey)
    {
        var result = await new SetModelPropertyHandler([new TmdlModelProvider()], config.Stores).HandleAsync(
            new SetModelPropertyRequest(
                new ModelReference(model), ".", [new ModelPropertyAssignment($"Annotation:{key}", json)],
                Type: null, Save: true, SaveTo: null, Serialization: ""),
            CancellationToken.None);
        Assert.True(result.Success, result.Diagnostics.FirstOrDefault()?.Message);
    }

    private static async Task<string?> EmbeddedAnnotationAsync(string model, string key = BpaModelRuleLoader.EmbeddedKey)
    {
        await using var session = await new TmdlModelProvider().OpenAsync(new ModelReference(model), CancellationToken.None);
        var snapshot = await session.GetSnapshotAsync(CancellationToken.None);
        return snapshot.Properties?.GetValueOrDefault($"Annotation:{key}");
    }
}
