using Tomix.App.Bpa;
using Tomix.Core.Bpa;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class BpaRulesIgnoreHandlerTests
{

    private static Tomix.App.Mutations.MutationStores TestStores => new(
        new Tomix.App.State.StagingStore(
            Path.Combine(Path.GetTempPath(), $"tomix-tests-{Guid.NewGuid():N}"), "test-session"),
        () => null);
    [Fact]
    public async Task Ignore_AddsRuleWritesCorrectKeyAndSaves()
    {
        var session = new CapturingSession(modelAnnotations: null);
        var handler = new BpaRulesIgnoreHandler([new Provider(session)], TestStores);

        var result = await handler.HandleAsync(
            new BpaRulesIgnoreRequest(new ModelReference("any"), "HIDE_FOREIGN_KEYS", Ignore: true, Save: true),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Data!.Changed);
        Assert.True(result.Data!.Saved);
        Assert.Contains("HIDE_FOREIGN_KEYS", result.Data.RuleIds);

        var write = Assert.Single(session.SetRequests);
        var correct = write.Properties.Single(p => p.Property == $"Annotation:{BpaIgnoreStore.Key}");
        Assert.Contains("HIDE_FOREIGN_KEYS", correct.Value);
    }

    [Fact]
    public async Task Ignore_MigratesLegacyKey_WritesCorrectAndEmptiesLegacy()
    {
        // Pre-existing ignore under the historical misspelled key.
        var session = new CapturingSession(modelAnnotations: new Dictionary<string, string>
        {
            [$"Annotation:{BpaIgnoreStore.LegacyKey}"] = "{\"RuleIDs\":[\"OLD_RULE\"]}"
        });
        var handler = new BpaRulesIgnoreHandler([new Provider(session)], TestStores);

        var result = await handler.HandleAsync(
            new BpaRulesIgnoreRequest(new ModelReference("any"), "HIDE_FOREIGN_KEYS", Ignore: true, Save: true),
            CancellationToken.None);

        // The migrated list keeps the legacy rule and adds the new one, under the correct key.
        Assert.Contains("OLD_RULE", result.Data!.RuleIds);
        Assert.Contains("HIDE_FOREIGN_KEYS", result.Data.RuleIds);

        var write = Assert.Single(session.SetRequests);
        var correct = write.Properties.Single(p => p.Property == $"Annotation:{BpaIgnoreStore.Key}");
        Assert.Contains("OLD_RULE", correct.Value);
        Assert.Contains("HIDE_FOREIGN_KEYS", correct.Value);

        // The misspelled key is removed (empty value).
        var legacy = write.Properties.Single(p => p.Property == $"Annotation:{BpaIgnoreStore.LegacyKey}");
        Assert.Equal("", legacy.Value);
    }

    [Fact]
    public async Task Unignore_RuleNotPresent_NoChangeNoWrite()
    {
        var session = new CapturingSession(modelAnnotations: null);
        var handler = new BpaRulesIgnoreHandler([new Provider(session)], TestStores);

        var result = await handler.HandleAsync(
            new BpaRulesIgnoreRequest(new ModelReference("any"), "RULE_A", Ignore: false, Save: true),
            CancellationToken.None);

        Assert.False(result.Data!.Changed);
        Assert.True(result.Data.Saved is bool b && !b);
        Assert.Empty(session.SetRequests);
    }

    [Fact]
    public async Task Ignore_UnknownRule_FailsWithoutWriting()
    {
        var session = new CapturingSession(modelAnnotations: null);
        var handler = new BpaRulesIgnoreHandler([new Provider(session)], TestStores);

        var result = await handler.HandleAsync(
            new BpaRulesIgnoreRequest(new ModelReference("any"), "HIDE_FOREIGN_KEY", Ignore: true, Save: true),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(2, result.ExitCode);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("TOMIX_BPA_RULE_NOT_FOUND", error.Code);
        Assert.StartsWith("Did you mean: HIDE_FOREIGN_KEYS?", error.Hint);
        Assert.Empty(session.SetRequests);
        Assert.False(session.Saved);
    }

    [Theory]
    // A rule the model defines itself is a known ID.
    [InlineData("MODEL_RULE", false, true)]
    // --allow-unknown lets any ID through.
    [InlineData("NOT_A_RULE", true, true)]
    [InlineData("NOT_A_RULE", false, false)]
    public async Task Ignore_ChecksModelRulesAndAllowUnknown(string ruleId, bool allowUnknown, bool success)
    {
        var session = new CapturingSession(new Dictionary<string, string>
        {
            [$"Annotation:{BpaModelRuleLoader.EmbeddedKey}"] =
                "[{\"ID\":\"MODEL_RULE\",\"Name\":\"m\",\"Category\":\"c\",\"Severity\":2,\"Scope\":\"Table\",\"Expression\":\"true\",\"CompatibilityLevel\":1200}]"
        });
        var handler = new BpaRulesIgnoreHandler([new Provider(session)], TestStores);

        var result = await handler.HandleAsync(
            new BpaRulesIgnoreRequest(new ModelReference("any"), ruleId, Ignore: true, AllowUnknown: allowUnknown),
            CancellationToken.None);

        Assert.Equal(success, result.Success);
    }

    [Fact]
    public async Task Ignore_UnreadableRuleSource_LetsUnknownIdThrough()
    {
        // A remote external rule file is never fetched, so its IDs can't be checked.
        var session = new CapturingSession(new Dictionary<string, string>
        {
            [$"Annotation:{BpaModelRuleLoader.ExternalFilesKey}"] = "[\"https://example.com/rules.json\"]"
        });
        var handler = new BpaRulesIgnoreHandler([new Provider(session)], TestStores);

        var result = await handler.HandleAsync(
            new BpaRulesIgnoreRequest(new ModelReference("any"), "REMOTE_RULE", Ignore: true),
            CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Ignore_RuleFromSelectedRulesFile_IsKnown()
    {
        using var dir = new TempDir();
        var rulesFile = dir.WriteFile("selected.json",
            "[{\"ID\":\"SELECTED_RULE\",\"Name\":\"m\",\"Category\":\"c\",\"Severity\":2,\"Scope\":\"Table\",\"Expression\":\"true\"}]");
        var session = new CapturingSession(modelAnnotations: null);
        var handler = new BpaRulesIgnoreHandler([new Provider(session)], TestStores, dir.Path);

        var result = await handler.HandleAsync(
            new BpaRulesIgnoreRequest(new ModelReference("any"), "SELECTED_RULE",
                Ignore: true, Save: true, RulesFile: rulesFile),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("SELECTED_RULE", result.Data!.RuleIds);
        Assert.Single(session.SetRequests);
    }

    [Fact]
    public async Task Unignore_UnknownRule_IsNotChecked()
    {
        var session = new CapturingSession(new Dictionary<string, string>
        {
            [$"Annotation:{BpaIgnoreStore.Key}"] = "{\"RuleIDs\":[\"GONE_RULE\"]}"
        });
        var handler = new BpaRulesIgnoreHandler([new Provider(session)], TestStores);

        var result = await handler.HandleAsync(
            new BpaRulesIgnoreRequest(new ModelReference("any"), "GONE_RULE", Ignore: false),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Data!.Changed);
    }

    [Fact]
    public async Task Ignore_NonMutationProvider_Fails()
    {
        var handler = new BpaRulesIgnoreHandler([new Provider(new ReadOnlySession())], TestStores);

        var result = await handler.HandleAsync(
            new BpaRulesIgnoreRequest(new ModelReference("any"), "RULE_A", Ignore: true),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_MUTATION_UNSUPPORTED_PROVIDER", result.Diagnostics[0].Code);
    }

    private sealed class Provider(IModelSession session) : IModelProvider
    {
        public bool CanOpen(ModelReference reference) => true;
        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken ct) => Task.FromResult(session);
    }

    private sealed class CapturingSession(IReadOnlyDictionary<string, string>? modelAnnotations)
        : IModelSession, IModelMutationSession
    {
        public string SourcePath => "";

        public List<ModelObjectSetRequest> SetRequests { get; } = [];
        public bool Saved { get; private set; }

        public Task<ModelSummary> GetSummaryAsync(CancellationToken ct)
            => Task.FromResult(new ModelSummary("M", 1601, 0, 0, 0, 0, 0));
        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken ct)
            => Task.FromResult(new ModelSnapshot("M", 1601, [], modelAnnotations));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ModelObjectMutationResult AddObject(ModelObjectAddRequest request) => throw new NotSupportedException();
        public ModelObjectMutationResult SetProperty(ModelObjectSetRequest request)
        {
            SetRequests.Add(request);
            return new ModelObjectMutationResult(request.Path, Changed: true);
        }
        public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request) => throw new NotSupportedException();
        public ModelReplaceResult ReplaceText(ModelReplaceRequest request) => throw new NotSupportedException();
        public Task<ModelExportResult> SaveAsync(string? outputPath, string serialization, bool force, CancellationToken ct)
        {
            Saved = true;
            return Task.FromResult(new ModelExportResult(outputPath ?? "source", serialization));
        }
    }

    private sealed class ReadOnlySession : IModelSession
    {
        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken ct)
            => Task.FromResult(new ModelSummary("M", 1601, 0, 0, 0, 0, 0));
        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken ct)
            => Task.FromResult(new ModelSnapshot("M", 1601, []));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
