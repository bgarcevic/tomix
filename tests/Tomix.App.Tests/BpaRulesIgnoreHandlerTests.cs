using Tomix.App.Bpa;
using Tomix.Core.Bpa;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class BpaRulesIgnoreHandlerTests
{
    [Fact]
    public async Task Ignore_AddsRuleWritesCorrectKeyAndSaves()
    {
        var session = new CapturingSession(modelAnnotations: null);
        var handler = new BpaRulesIgnoreHandler([new Provider(session)]);

        var result = await handler.HandleAsync(
            new BpaRulesIgnoreRequest(new ModelReference("any"), "RULE_A", Ignore: true, Save: true),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Data!.Changed);
        Assert.True(result.Data!.Saved is string or true);
        Assert.Contains("RULE_A", result.Data.RuleIds);

        var write = Assert.Single(session.SetRequests);
        var correct = write.Properties.Single(p => p.Property == $"Annotation:{BpaIgnoreStore.Key}");
        Assert.Contains("RULE_A", correct.Value);
    }

    [Fact]
    public async Task Ignore_MigratesLegacyKey_WritesCorrectAndEmptiesLegacy()
    {
        // Pre-existing ignore under the historical misspelled key.
        var session = new CapturingSession(modelAnnotations: new Dictionary<string, string>
        {
            [$"Annotation:{BpaIgnoreStore.LegacyKey}"] = "{\"RuleIDs\":[\"OLD_RULE\"]}"
        });
        var handler = new BpaRulesIgnoreHandler([new Provider(session)]);

        var result = await handler.HandleAsync(
            new BpaRulesIgnoreRequest(new ModelReference("any"), "RULE_A", Ignore: true, Save: true),
            CancellationToken.None);

        // The migrated list keeps the legacy rule and adds the new one, under the correct key.
        Assert.Contains("OLD_RULE", result.Data!.RuleIds);
        Assert.Contains("RULE_A", result.Data.RuleIds);

        var write = Assert.Single(session.SetRequests);
        var correct = write.Properties.Single(p => p.Property == $"Annotation:{BpaIgnoreStore.Key}");
        Assert.Contains("OLD_RULE", correct.Value);
        Assert.Contains("RULE_A", correct.Value);

        // The misspelled key is removed (empty value).
        var legacy = write.Properties.Single(p => p.Property == $"Annotation:{BpaIgnoreStore.LegacyKey}");
        Assert.Equal("", legacy.Value);
    }

    [Fact]
    public async Task Unignore_RuleNotPresent_NoChangeNoWrite()
    {
        var session = new CapturingSession(modelAnnotations: null);
        var handler = new BpaRulesIgnoreHandler([new Provider(session)]);

        var result = await handler.HandleAsync(
            new BpaRulesIgnoreRequest(new ModelReference("any"), "RULE_A", Ignore: false, Save: true),
            CancellationToken.None);

        Assert.False(result.Data!.Changed);
        Assert.True(result.Data.Saved is bool b && !b);
        Assert.Empty(session.SetRequests);
    }

    [Fact]
    public async Task Ignore_NonMutationProvider_Fails()
    {
        var handler = new BpaRulesIgnoreHandler([new Provider(new ReadOnlySession())]);

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
