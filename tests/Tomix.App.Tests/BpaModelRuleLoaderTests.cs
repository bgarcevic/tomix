using Tomix.App.Bpa;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class BpaModelRuleLoaderTests
{
    private const string OneRuleJson =
        "[{\"ID\":\"R1\",\"Name\":\"r1\",\"Category\":\"c\",\"Severity\":2,\"Scope\":\"Table\",\"Expression\":\"true\",\"CompatibilityLevel\":1200}]";

    private static Dictionary<string, string> Annotations(params (string Name, string Value)[] entries)
        => entries.ToDictionary(e => $"Annotation:{e.Name}", e => e.Value);

    [Fact]
    public async Task LoadAsync_EmbeddedRules_AreParsed()
    {
        var props = Annotations((BpaModelRuleLoader.EmbeddedKey, OneRuleJson));

        var outcome = await BpaModelRuleLoader.LoadAsync(props, baseDirectory: null, allowExternal: false, CancellationToken.None);

        var collection = Assert.Single(outcome.Collections);
        Assert.Equal(BpaRuleSourceKind.ModelEmbedded, collection.Kind);
        Assert.Equal("R1", Assert.Single(collection.Rules).Id);
        Assert.Empty(outcome.Diagnostics);
    }

    [Fact]
    public async Task LoadAsync_EmbeddedLegacyMisspelledKey_IsHonored()
    {
        var props = Annotations((BpaModelRuleLoader.EmbeddedLegacyKey, OneRuleJson));

        var outcome = await BpaModelRuleLoader.LoadAsync(props, null, false, CancellationToken.None);

        Assert.Equal(BpaRuleSourceKind.ModelEmbedded, Assert.Single(outcome.Collections).Kind);
    }

    [Fact]
    public async Task LoadAsync_MalformedEmbeddedJson_ReportsDiagnosticAndContinues()
    {
        var props = Annotations((BpaModelRuleLoader.EmbeddedKey, "{ not valid json"));

        var outcome = await BpaModelRuleLoader.LoadAsync(props, null, false, CancellationToken.None);

        Assert.Empty(outcome.Collections);
        Assert.Single(outcome.Diagnostics);
    }

    [Fact]
    public async Task LoadAsync_MalformedExternalAnnotation_ReportsDiagnosticAndDoesNotThrow()
    {
        // Spec test N: invalid JSON in the external-files annotation must not crash analysis.
        var props = Annotations((BpaModelRuleLoader.ExternalFilesKey, "not-an-array"));

        var outcome = await BpaModelRuleLoader.LoadAsync(props, null, false, CancellationToken.None);

        Assert.Empty(outcome.Collections);
        Assert.Contains(outcome.Diagnostics, d => d.Contains(BpaModelRuleLoader.ExternalFilesKey));
    }

    [Fact]
    public async Task LoadAsync_ExternalLocalFile_ResolvedRelativeToBaseDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tomix-bpa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "rules.json"), OneRuleJson);
            var props = Annotations((BpaModelRuleLoader.ExternalFilesKey, "[\"rules.json\"]"));

            var outcome = await BpaModelRuleLoader.LoadAsync(props, dir, allowExternal: false, CancellationToken.None);

            var collection = Assert.Single(outcome.Collections);
            Assert.Equal(BpaRuleSourceKind.External, collection.Kind);
            Assert.Equal("R1", Assert.Single(collection.Rules).Id);
            Assert.Empty(outcome.Diagnostics);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_WindowsStylePath_ResolvesOnEveryPlatform()
    {
        // Community tooling writes external-file paths with Windows separators
        // (e.g. "..\\.devops\\bpa-rules.json"); they must resolve on Unix too.
        var root = Path.Combine(Path.GetTempPath(), "tomix-bpa-" + Guid.NewGuid().ToString("N"));
        var modelDir = Path.Combine(root, "model");
        Directory.CreateDirectory(modelDir);
        Directory.CreateDirectory(Path.Combine(root, ".devops"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, ".devops", "bpa-rules.json"), OneRuleJson);
            var props = Annotations((BpaModelRuleLoader.ExternalFilesKey, "[\"..\\\\.devops\\\\bpa-rules.json\"]"));

            var outcome = await BpaModelRuleLoader.LoadAsync(props, modelDir, allowExternal: false, CancellationToken.None);

            var collection = Assert.Single(outcome.Collections);
            Assert.Equal(BpaRuleSourceKind.External, collection.Kind);
            // The display name stays as-written so precedence identity and diagnostics match the annotation.
            Assert.Equal("..\\.devops\\bpa-rules.json", collection.DisplayName);
            Assert.Empty(outcome.Diagnostics);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_MissingExternalFile_ReportsDiagnosticWithRemedies()
    {
        var props = Annotations((BpaModelRuleLoader.ExternalFilesKey, "[\"does-not-exist.json\"]"));

        var outcome = await BpaModelRuleLoader.LoadAsync(props, Path.GetTempPath(), false, CancellationToken.None);

        Assert.Empty(outcome.Collections);
        var diagnostic = Assert.Single(outcome.Diagnostics);
        Assert.Contains("does-not-exist.json", diagnostic);
        Assert.Contains("--no-model-rules", diagnostic);
        Assert.Contains(BpaModelRuleLoader.ExternalFilesKey, diagnostic);
    }

    [Fact]
    public async Task LoadAsync_RemoteFileWithoutOptIn_IsSkippedWithDiagnostic()
    {
        var props = Annotations((BpaModelRuleLoader.ExternalFilesKey, "[\"https://example.org/rules.json\"]"));

        var outcome = await BpaModelRuleLoader.LoadAsync(props, null, allowExternal: false, CancellationToken.None);

        Assert.Empty(outcome.Collections);
        Assert.Contains(outcome.Diagnostics, d => d.Contains("--allow-external-rules"));
    }

    [Fact]
    public void ResolveBaseDirectory_SessionFolder_WinsOverEntryPoint()
    {
        // The entry point may be a project root or .pbip; the session opens the nested
        // definition folder, and that is what relative rule paths are anchored to.
        var root = Path.Combine(Path.GetTempPath(), "tomix-bpa-" + Guid.NewGuid().ToString("N"));
        var definition = Path.Combine(root, "Sales.SemanticModel", "definition");
        Directory.CreateDirectory(definition);
        try
        {
            var resolved = BpaModelRuleLoader.ResolveBaseDirectory(
                new StubSession(definition), new ModelReference(root));

            Assert.Equal(definition, resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveBaseDirectory_SessionFile_UsesContainingFolder()
    {
        // A .bim session reports the file itself as its source path.
        var dir = Path.Combine(Path.GetTempPath(), "tomix-bpa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var bim = Path.Combine(dir, "model.bim");
            File.WriteAllText(bim, "{}");

            var resolved = BpaModelRuleLoader.ResolveBaseDirectory(
                new StubSession(bim), new ModelReference(bim));

            Assert.Equal(dir, resolved);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveBaseDirectory_NullSession_FallsBackToLocalReference()
    {
        // A staged run passes no session so the original model's folder is used, not the working copy.
        var dir = Path.Combine(Path.GetTempPath(), "tomix-bpa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal(dir, BpaModelRuleLoader.ResolveBaseDirectory(session: null, new ModelReference(dir)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveBaseDirectory_ConnectedSession_HasNoBaseDirectory()
    {
        // A connected model reports no source path and is not a local path: entries stay cwd-relative.
        var resolved = BpaModelRuleLoader.ResolveBaseDirectory(
            new StubSession(""), new ModelReference("powerbi://api.powerbi.com/v1.0/myorg/WS", "Sales"));

        Assert.Null(resolved);
    }

    [Fact]
    public async Task LoadAsync_ListContext_HintsOnlySuggestOptionsListAccepts()
    {
        // "bpa rules list" accepts neither --no-model-rules nor --allow-external-rules, so
        // its diagnostics must not tell the user to pass them to the current command.
        var props = Annotations((BpaModelRuleLoader.ExternalFilesKey,
            "[\"does-not-exist.json\", \"https://example.org/rules.json\"]"));

        var outcome = await BpaModelRuleLoader.LoadAsync(
            props, Path.GetTempPath(), allowExternal: false, BpaRuleHintContext.List,
            httpClient: null, CancellationToken.None);

        Assert.Equal(2, outcome.Diagnostics.Count);
        var notFound = outcome.Diagnostics[0];
        Assert.Contains("does-not-exist.json", notFound);
        Assert.DoesNotContain("--no-model-rules", notFound);
        Assert.Contains(BpaModelRuleLoader.ExternalFilesKey, notFound);
        var remoteSkipped = outcome.Diagnostics[1];
        Assert.Contains("bpa run --allow-external-rules", remoteSkipped);
    }

    private sealed class StubSession(string sourcePath) : IModelSession
    {
        public string SourcePath => sourcePath;

        public Task<ModelSummary> GetSummaryAsync(CancellationToken ct)
            => Task.FromResult(new ModelSummary("M", 1601, 0, 0, 0, 0, 0));
        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken ct)
            => Task.FromResult(new ModelSnapshot("M", 1601, [], new Dictionary<string, string>()));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
