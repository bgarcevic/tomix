using Tomix.App.Bpa;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;

namespace Tomix.App.Tests;

/// <summary>
/// Staged BPA runs resolve model-carried external rule paths against the original model rather
/// than the working copy, which means probing the original — a best-effort step that must never
/// fail the run.
/// </summary>
public sealed class BpaRunHandlerTests
{
    private const string OneRuleJson =
        "[{\"ID\":\"TEAM_RULE\",\"Name\":\"team rule\",\"Category\":\"c\",\"Severity\":2,\"Scope\":\"Table\",\"Expression\":\"false\",\"CompatibilityLevel\":1200}]";

    [Fact]
    public async Task StagedRun_ProviderThrowsClaimingTheOriginal_StillCompletes()
    {
        using var config = new TempConfigDir();
        using var root = new TempDir();
        var model = SampleModel.CopyTo(root, "model");
        root.WriteFile(Path.Combine(".devops", "bpa-rules.json"), OneRuleJson);
        await AddExternalRuleAnnotationAsync(model, "..\\\\.devops\\\\bpa-rules.json");

        var stores = config.Stores;
        var userRules = new BpaUserRuleState(config.Path);
        var reference = new ModelReference(model);
        var request = new BpaRunRequest(reference, NoDefaults: true, Fix: true, Stage: true);

        // Stage once with a well-behaved provider so a working copy exists.
        var staged = await new BpaRunHandler([new TmdlModelProvider()], stores, userRules, config.Path)
            .HandleAsync(request, CancellationToken.None);
        Assert.True(staged.Success, string.Join("; ", staged.Diagnostics.Select(d => d.Message)));

        // Re-run against the existing working copy with a provider that throws while being
        // asked whether it can open the original — as the real TMDL provider does for an
        // unreadable .pbip. The run must degrade, not abort.
        var handler = new BpaRunHandler(
            [new TmdlModelProvider(), new ThrowsWhenClaiming(model)], stores, userRules, config.Path);

        var result = await handler.HandleAsync(request, CancellationToken.None);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
    }

    private static async Task AddExternalRuleAnnotationAsync(string modelFolder, string entry)
    {
        var path = Path.Combine(modelFolder, "model.tmdl");
        var lines = (await File.ReadAllLinesAsync(path)).ToList();
        // Model-level annotations sit at the top level, beside the existing ones.
        var anchor = lines.FindIndex(l => l.StartsWith("annotation ", StringComparison.Ordinal));
        lines.Insert(
            anchor < 0 ? lines.Count : anchor + 1,
            $"annotation {BpaModelRuleLoader.ExternalFilesKey} = [\"{entry}\"]");
        await File.WriteAllLinesAsync(path, lines);
    }

    /// <summary>A provider that fails while inspecting one specific path, like an unreadable file.</summary>
    private sealed class ThrowsWhenClaiming(string path) : IModelProvider
    {
        public bool CanOpen(ModelReference reference)
            => reference.Value == path
                ? throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.")
                : false;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
