using System.Text.Json.Nodes;
using Tomix.App.Bpa;
using Tomix.Core.Bpa;

namespace Tomix.App.Tests;

public sealed class BpaRulesAuthoringTests
{
    private static readonly BpaRuleFields NewRule = new(
        Name: "Measures need a description",
        Scope: "measure, calculatedcolumn",
        Expression: "string.IsNullOrWhitespace(Description)",
        Severity: "error");

    [Fact]
    public void Add_CreatesUserFile_InCatalogShape_AndRunLoadsIt()
    {
        using var dir = new TempDir();

        var result = new BpaRulesAddHandler(dir.Path).Handle(new BpaRulesAddRequest("MY_RULE", NewRule));

        Assert.True(result.Success, result.Diagnostics.FirstOrDefault()?.Message);
        var path = Path.Combine(dir.Path, "bpa-rules.json");
        Assert.Equal(path, result.Data!.Path);
        Assert.Equal(1, result.Data.RuleCount);

        var rule = Assert.Single(BpaRuleLoader.LoadFromFile(path));
        Assert.Equal("MY_RULE", rule.Id);
        Assert.Equal(BpaSeverity.Error, rule.Severity);
        Assert.Equal(["Measure", "CalculatedColumn"], rule.Scope);   // canonical casing
        Assert.Equal(BpaRulesAddHandler.DefaultCategory, rule.Category);

        var keys = ((JsonObject)JsonNode.Parse(File.ReadAllText(path))!.AsArray()[0]!).Select(p => p.Key);
        Assert.Equal(["ID", "Name", "Category", "Severity", "Scope", "Expression", "CompatibilityLevel"], keys);
    }

    [Fact]
    public void RoundTrip_InitAddListSetRemove()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "team-rules.json");

        Assert.True(new BpaRulesInitHandler(dir.Path).Handle(new BpaRulesInitRequest(file)).Success);
        Assert.Equal("[]", File.ReadAllText(file).Trim());

        Assert.True(new BpaRulesAddHandler(dir.Path).Handle(new BpaRulesAddRequest("MY_RULE", NewRule, file)).Success);

        var listed = ListCustom(file);
        var info = Assert.Single(listed);
        Assert.Equal("Measure, CalculatedColumn", info.Scope);

        var set = new BpaRulesSetHandler(dir.Path).Handle(new BpaRulesSetRequest(
            "my_rule", new BpaRuleFields(Severity: "info", Scope: "Table", FixExpression: "IsHidden = true"), file));
        Assert.True(set.Success, set.Diagnostics.FirstOrDefault()?.Message);
        Assert.Equal(["Severity", "Scope", "FixExpression"], set.Data!.ChangedFields);
        var updated = Assert.Single(ListCustom(file));
        Assert.Equal(BpaSeverity.Info, updated.Severity);
        Assert.Equal("Table", updated.Scope);
        Assert.Equal("IsHidden = true", updated.FixExpression);

        var removed = new BpaRulesRemoveHandler(dir.Path).Handle(new BpaRulesRemoveRequest("MY_RULE", file));
        Assert.True(removed.Success);
        Assert.Equal(0, removed.Data!.RuleCount);
        Assert.Empty(ListCustom(file));
    }

    [Fact]
    public void Set_SameValues_ReportsNoChange_AndLeavesFileUntouched()
    {
        using var dir = new TempDir();
        new BpaRulesAddHandler(dir.Path).Handle(new BpaRulesAddRequest("MY_RULE", NewRule));
        var path = Path.Combine(dir.Path, "bpa-rules.json");
        var before = File.GetLastWriteTimeUtc(path);
        File.SetLastWriteTimeUtc(path, before.AddMinutes(-5));

        var result = new BpaRulesSetHandler(dir.Path).Handle(
            new BpaRulesSetRequest("MY_RULE", new BpaRuleFields(Severity: "3", Name: NewRule.Name)));

        Assert.True(result.Success);
        Assert.False(result.Data!.Changed);
        Assert.Equal(before.AddMinutes(-5), File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Set_EmptyOptionalField_RemovesIt_AndKeepsUnknownFields()
    {
        using var dir = new TempDir();
        var file = dir.WriteFile("rules.json",
            "[{\"ID\":\"R\",\"Name\":\"N\",\"Category\":\"C\",\"Severity\":2,\"Scope\":\"Table\","
            + "\"Expression\":\"true\",\"FixExpression\":\"IsHidden = true\",\"Remarks\":\"keep me\"}]");

        var result = new BpaRulesSetHandler(dir.Path).Handle(
            new BpaRulesSetRequest("R", new BpaRuleFields(FixExpression: ""), file));

        Assert.True(result.Success);
        var rule = (JsonObject)JsonNode.Parse(File.ReadAllText(file))!.AsArray()[0]!;
        Assert.False(rule.ContainsKey("FixExpression"));
        Assert.Equal("keep me", rule["Remarks"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Measure, Widget", "TOMIX_BPA_RULE_INVALID_SCOPE")]
    [InlineData(" , ", "TOMIX_BPA_RULE_INVALID_SCOPE")]
    public void Add_InvalidScope_Fails_AndWritesNothing(string scope, string code)
    {
        using var dir = new TempDir();

        var result = new BpaRulesAddHandler(dir.Path).Handle(
            new BpaRulesAddRequest("MY_RULE", NewRule with { Scope = scope }));

        Assert.False(result.Success);
        Assert.Equal(code, result.Diagnostics[0].Code);
        Assert.False(File.Exists(Path.Combine(dir.Path, "bpa-rules.json")));
    }

    [Theory]
    [InlineData("critical")]
    [InlineData("4")]
    public void Set_InvalidSeverity_Fails(string severity)
    {
        using var dir = new TempDir();
        new BpaRulesAddHandler(dir.Path).Handle(new BpaRulesAddRequest("MY_RULE", NewRule));

        var result = new BpaRulesSetHandler(dir.Path).Handle(
            new BpaRulesSetRequest("MY_RULE", new BpaRuleFields(Severity: severity)));

        Assert.Equal("TOMIX_BPA_RULE_INVALID_SEVERITY", result.Diagnostics[0].Code);
    }

    [Fact]
    public void Add_MissingRequiredFields_NamesThem()
    {
        using var dir = new TempDir();

        var result = new BpaRulesAddHandler(dir.Path).Handle(
            new BpaRulesAddRequest("MY_RULE", new BpaRuleFields(Name: "N")));

        Assert.Equal("TOMIX_BPA_RULE_FIELD_REQUIRED", result.Diagnostics[0].Code);
        Assert.Contains("--scope", result.Diagnostics[0].Message);
        Assert.Contains("--expression", result.Diagnostics[0].Message);
    }

    [Fact]
    public void Add_DuplicateId_Fails()
    {
        using var dir = new TempDir();
        var handler = new BpaRulesAddHandler(dir.Path);
        handler.Handle(new BpaRulesAddRequest("MY_RULE", NewRule));

        var result = handler.Handle(new BpaRulesAddRequest("my_rule", NewRule));

        Assert.Equal("TOMIX_BPA_RULE_EXISTS", result.Diagnostics[0].Code);
    }

    [Fact]
    public void Remove_BuiltInRule_FailsWithDisableHint()
    {
        using var dir = new TempDir();
        new BpaRulesInitHandler(dir.Path).Handle(new BpaRulesInitRequest());

        var result = new BpaRulesRemoveHandler(dir.Path).Handle(new BpaRulesRemoveRequest("HIDE_FOREIGN_KEYS"));

        Assert.Equal("TOMIX_BPA_RULE_NOT_FOUND", result.Diagnostics[0].Code);
        Assert.Contains("bpa rules ignore HIDE_FOREIGN_KEYS --user", result.Diagnostics[0].Hint);
    }

    [Fact]
    public void Set_MissingFile_FailsWithInitHint()
    {
        using var dir = new TempDir();

        var result = new BpaRulesSetHandler(dir.Path).Handle(
            new BpaRulesSetRequest("MY_RULE", new BpaRuleFields(Severity: "info")));

        Assert.Equal("TOMIX_BPA_RULES_FILE_NOT_FOUND", result.Diagnostics[0].Code);
    }

    [Fact]
    public void Init_ExistingFile_FailsUnlessOverwrite()
    {
        using var dir = new TempDir();
        new BpaRulesAddHandler(dir.Path).Handle(new BpaRulesAddRequest("MY_RULE", NewRule));
        var handler = new BpaRulesInitHandler(dir.Path);

        Assert.Equal("TOMIX_BPA_RULES_FILE_EXISTS", handler.Handle(new BpaRulesInitRequest()).Diagnostics[0].Code);
        Assert.True(handler.Handle(new BpaRulesInitRequest(Overwrite: true)).Success);
        Assert.Empty(BpaRuleLoader.LoadFromFile(Path.Combine(dir.Path, "bpa-rules.json")));
    }

    [Fact]
    public void RemoteRulesFile_IsRejected()
    {
        using var dir = new TempDir();

        var result = new BpaRulesAddHandler(dir.Path).Handle(
            new BpaRulesAddRequest("MY_RULE", NewRule, "https://example.com/rules.json"));

        Assert.Equal("TOMIX_BPA_RULES_FILE_REMOTE", result.Diagnostics[0].Code);
    }

    [Fact]
    public void MalformedFile_FailsToLoad()
    {
        using var dir = new TempDir();
        var file = dir.WriteFile("rules.json", "{\"not\":\"an array\"}");

        var result = new BpaRulesRemoveHandler(dir.Path).Handle(new BpaRulesRemoveRequest("R", file));

        Assert.Equal("TOMIX_BPA_RULES_LOAD_FAILED", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task List_IncludesUserFile_AsUserSource()
    {
        using var dir = new TempDir();
        new BpaRulesAddHandler(dir.Path).Handle(new BpaRulesAddRequest("MY_RULE", NewRule));

        var result = await new BpaRulesListHandler(
                null, new BpaUserRuleState(dir.Path), configDirectory: dir.Path)
            .HandleAsync(new BpaRulesListRequest(NoDefaults: true), CancellationToken.None);

        var rule = Assert.Single(result.Data!.Rules);
        Assert.Equal("user", rule.Source);
        Assert.Equal("MY_RULE", rule.Id);
    }

    private static IReadOnlyList<BpaRuleInfo> ListCustom(string file)
    {
        using var state = new TempDir();
        return new BpaRulesListHandler(null, new BpaUserRuleState(state.Path))
            .HandleAsync(new BpaRulesListRequest(RulesFile: file, NoDefaults: true), CancellationToken.None)
            .GetAwaiter().GetResult()
            .Data!.Rules;
    }
}
