using Tomix.App.Bpa;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class BpaRulesListHandlerTests
{
    private const string OneRuleJson =
        "[{\"ID\":\"MODEL_RULE\",\"Name\":\"model rule\",\"Category\":\"c\",\"Severity\":2,\"Scope\":\"Table\",\"Expression\":\"true\",\"CompatibilityLevel\":1200}]";

    [Fact]
    public async Task List_WithModel_IncludesModelRuleSourcesAndDiagnostics()
    {
        using var dir = new TempDir();
        var session = new SnapshotSession(new Dictionary<string, string>
        {
            [$"Annotation:{BpaModelRuleLoader.EmbeddedKey}"] = OneRuleJson,
            [$"Annotation:{BpaModelRuleLoader.ExternalFilesKey}"] = "[\"missing-rules.json\"]"
        });
        var handler = new BpaRulesListHandler([new Provider(session)], new BpaUserRuleState(dir.Path));

        var result = await handler.HandleAsync(
            new BpaRulesListRequest(Model: new ModelReference(dir.Path), NoDefaults: true),
            CancellationToken.None);

        Assert.True(result.Success);
        var rule = Assert.Single(result.Data!.Rules);
        Assert.Equal("MODEL_RULE", rule.Id);
        Assert.Equal("model-embedded", rule.Source);
        var diagnostic = Assert.Single(result.Data.Diagnostics!);
        Assert.Contains("missing-rules.json", diagnostic);
        // "rules list" has no --no-model-rules option; the hint must not suggest it.
        Assert.DoesNotContain("--no-model-rules", diagnostic);
    }

    [Fact]
    public async Task List_ExternalFiles_ResolveAgainstOpenedSourcePath()
    {
        // A .pbip/.pbism/project-root entry point opens the nested definition folder; relative
        // external-file entries are anchored there, not at the entry point the user typed.
        using var root = new TempDir();
        var definition = root.CreateSubdirectory("Sales.SemanticModel", "definition");
        root.WriteFile("rules.json", OneRuleJson.Replace("MODEL_RULE", "EXTERNAL_RULE"));

        var session = new SnapshotSession(new Dictionary<string, string>
        {
            [$"Annotation:{BpaModelRuleLoader.ExternalFilesKey}"] = "[\"..\\\\..\\\\rules.json\"]"
        }, sourcePath: definition);
        var handler = new BpaRulesListHandler([new Provider(session)], new BpaUserRuleState(root.Path));

        var result = await handler.HandleAsync(
            new BpaRulesListRequest(Model: new ModelReference(root.Path), NoDefaults: true),
            CancellationToken.None);

        Assert.True(result.Success);
        var rule = Assert.Single(result.Data!.Rules);
        Assert.Equal("EXTERNAL_RULE", rule.Id);
        Assert.Null(result.Data.Diagnostics);
    }

    [Fact]
    public async Task List_WithoutModel_HasNoDiagnostics()
    {
        using var dir = new TempDir();
        var handler = new BpaRulesListHandler([], new BpaUserRuleState(dir.Path));

        var result = await handler.HandleAsync(new BpaRulesListRequest(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotEmpty(result.Data!.Rules);
        Assert.Null(result.Data.Diagnostics);
    }

    private const string TwoRulesJson =
        "[{\"ID\":\"RULE_A\",\"Name\":\"a\",\"Category\":\"c\",\"Severity\":2,\"Scope\":\"Table\",\"Expression\":\"true\",\"CompatibilityLevel\":1200},"
        + "{\"ID\":\"RULE_B\",\"Name\":\"b\",\"Category\":\"c\",\"Severity\":2,\"Scope\":\"Table\",\"Expression\":\"true\",\"CompatibilityLevel\":1200},"
        + "{\"ID\":\"RULE_C\",\"Name\":\"c\",\"Category\":\"c\",\"Severity\":2,\"Scope\":\"Table\",\"Expression\":\"true\",\"CompatibilityLevel\":1200}]";

    // RULE_A is on the model's ignore list, RULE_B is disabled for the user, RULE_C is active.
    private static async Task<Tomix.Core.Results.TomixResult<BpaRulesListResult>> ListWithIgnoredAndDisabled(
        TempDir dir, bool all = false, bool ignoredOnly = false, bool disabledOnly = false, string? ruleId = null)
    {
        var session = new SnapshotSession(new Dictionary<string, string>
        {
            [$"Annotation:{BpaModelRuleLoader.EmbeddedKey}"] = TwoRulesJson,
            [$"Annotation:{Tomix.Core.Bpa.BpaIgnoreStore.Key}"] = "{\"RuleIDs\":[\"RULE_A\"]}"
        });
        var userRules = new BpaUserRuleState(dir.Path);
        userRules.Disable("RULE_B");
        var handler = new BpaRulesListHandler([new Provider(session)], userRules);

        return await handler.HandleAsync(
            new BpaRulesListRequest(
                Model: new ModelReference(dir.Path), All: all, NoDefaults: true,
                IgnoredOnly: ignoredOnly, DisabledOnly: disabledOnly, RuleId: ruleId),
            CancellationToken.None);
    }

    [Fact]
    public async Task List_SeparatesIgnoredFromDisabled()
    {
        using var dir = new TempDir();

        var result = await ListWithIgnoredAndDisabled(dir, all: true);

        var statuses = result.Data!.Rules.ToDictionary(r => r.Id, r => r.Status);
        Assert.Equal("ignored", statuses["RULE_A"]);
        Assert.Equal("disabled", statuses["RULE_B"]);
        Assert.Equal("active", statuses["RULE_C"]);
        Assert.Equal(new BpaRulesSummary(Total: 3, Active: 1, Disabled: 1, Ignored: 1), result.Data.Summary);
    }

    [Theory]
    [InlineData(false, false, "RULE_C")]
    [InlineData(true, false, "RULE_A")]
    [InlineData(false, true, "RULE_B")]
    [InlineData(true, true, "RULE_A,RULE_B")]
    public async Task List_FiltersByStatus(bool ignoredOnly, bool disabledOnly, string expected)
    {
        using var dir = new TempDir();

        var result = await ListWithIgnoredAndDisabled(dir, ignoredOnly: ignoredOnly, disabledOnly: disabledOnly);

        Assert.Equal(expected, string.Join(",", result.Data!.Rules.Select(r => r.Id).Order()));
    }

    [Fact]
    public async Task Show_FindsRuleCaseInsensitivelyWhateverItsStatus()
    {
        using var dir = new TempDir();

        var result = await ListWithIgnoredAndDisabled(dir, ruleId: "rule_a");

        Assert.True(result.Success);
        var rule = Assert.Single(result.Data!.Rules);
        Assert.Equal("RULE_A", rule.Id);
        Assert.Equal("ignored", rule.Status);
    }

    [Theory]
    [InlineData("RULE", "Did you mean: RULE_A, RULE_B, RULE_C?")]
    [InlineData("NOPE", "Run 'tx bpa rules list --all' to see every rule ID.")]
    public async Task Show_UnknownRule_FailsWithHint(string ruleId, string hint)
    {
        using var dir = new TempDir();

        var result = await ListWithIgnoredAndDisabled(dir, ruleId: ruleId);

        Assert.False(result.Success);
        Assert.Equal(2, result.ExitCode);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("TOMIX_BPA_RULE_NOT_FOUND", error.Code);
        Assert.Equal(hint, error.Hint);
    }

    private sealed class Provider(IModelSession session) : IModelProvider
    {
        public bool CanOpen(ModelReference reference) => true;
        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken ct) => Task.FromResult(session);
    }

    private sealed class SnapshotSession(
        IReadOnlyDictionary<string, string> modelProperties,
        string sourcePath = "") : IModelSession
    {
        public string SourcePath => sourcePath;

        public Task<ModelSummary> GetSummaryAsync(CancellationToken ct)
            => Task.FromResult(new ModelSummary("M", 1601, 0, 0, 0, 0, 0));
        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken ct)
            => Task.FromResult(new ModelSnapshot("M", 1601, [], modelProperties));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
