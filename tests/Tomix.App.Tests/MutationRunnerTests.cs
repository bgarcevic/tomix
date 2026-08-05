using Tomix.App.Mutations;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;

namespace Tomix.App.Tests;

/// <summary>
/// MutationRunner must use the stores it is given — never ambient state. These tests run the
/// real TMDL provider against a sample copy and assert the staged working copy lands under the
/// injected staging store's directory.
/// </summary>
public sealed class MutationRunnerTests
{
    private static readonly MutationOptions StageOptions =
        new(Save: false, SaveTo: null, Stage: true, Revert: false, Serialization: "", Force: false, NoSync: true);

    private static readonly MutationOptions RevertOptions =
        new(Save: false, SaveTo: null, Stage: false, Revert: true, Serialization: "", Force: false, NoSync: true);

    [Fact]
    public async Task RunAsync_Stage_WritesToInjectedStagingStore()
    {
        using var config = new TempConfigDir();
        using var model = SampleModel.CopyToTemp();

        var staging = config.Staging;
        var result = await RunAsync(new ModelReference(model.Path), StageOptions, config.Stores);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.NotNull(staging.TryLoad(new ModelReference(model.Path)));
        Assert.True(Directory.Exists(config.Combine("staging", TempConfigDir.SessionId)));
    }

    [Fact]
    public async Task RunAsync_Revert_DiscardsFromInjectedStagingStore()
    {
        using var config = new TempConfigDir();
        using var model = SampleModel.CopyToTemp();

        var staging = config.Staging;
        var reference = new ModelReference(model.Path);

        await RunAsync(reference, StageOptions, config.Stores);
        Assert.NotNull(staging.TryLoad(reference));

        var revert = await RunAsync(reference, RevertOptions, config.Stores);

        Assert.True(revert.Success);
        Assert.Null(staging.TryLoad(reference));
    }

    private static Task<Tomix.Core.Results.TomixResult<string>> RunAsync(
        ModelReference model, MutationOptions options, MutationStores stores)
        => MutationRunner.RunAsync(
            [new TmdlModelProvider()], model, options, "set", stores,
            (_, _, _) => Task.FromResult<(bool, string, Func<MutationOutcome, string>)>(
                (true, "test mutation", _ => "mutated")),
            revertResult: "reverted",
            CancellationToken.None);
}
