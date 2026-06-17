using Tomix.App.Bpa;

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
    public async Task LoadAsync_MissingExternalFile_ReportsDiagnostic()
    {
        var props = Annotations((BpaModelRuleLoader.ExternalFilesKey, "[\"does-not-exist.json\"]"));

        var outcome = await BpaModelRuleLoader.LoadAsync(props, Path.GetTempPath(), false, CancellationToken.None);

        Assert.Empty(outcome.Collections);
        Assert.Contains(outcome.Diagnostics, d => d.Contains("does-not-exist.json"));
    }

    [Fact]
    public async Task LoadAsync_RemoteFileWithoutOptIn_IsSkippedWithDiagnostic()
    {
        var props = Annotations((BpaModelRuleLoader.ExternalFilesKey, "[\"https://example.org/rules.json\"]"));

        var outcome = await BpaModelRuleLoader.LoadAsync(props, null, allowExternal: false, CancellationToken.None);

        Assert.Empty(outcome.Collections);
        Assert.Contains(outcome.Diagnostics, d => d.Contains("--allow-external-rules"));
    }
}
