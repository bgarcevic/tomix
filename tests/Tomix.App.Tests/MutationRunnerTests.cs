using Tomix.App.Mutations;
using Tomix.App.Tests.Support;
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
        var model = CopySample();
        try
        {
            var staging = config.Staging;
            var result = await RunAsync(new ModelReference(model), StageOptions, new MutationStores(staging, () => null));

            Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
            Assert.NotNull(staging.TryLoad(new ModelReference(model)));
            Assert.True(Directory.Exists(Path.Combine(config.Path, "staging", "test-session")));
        }
        finally
        {
            Directory.Delete(model, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Revert_DiscardsFromInjectedStagingStore()
    {
        using var config = new TempConfigDir();
        var model = CopySample();
        try
        {
            var staging = config.Staging;
            var stores = new MutationStores(staging, () => null);
            var reference = new ModelReference(model);

            await RunAsync(reference, StageOptions, stores);
            Assert.NotNull(staging.TryLoad(reference));

            var revert = await RunAsync(reference, RevertOptions, stores);

            Assert.True(revert.Success);
            Assert.Null(staging.TryLoad(reference));
        }
        finally
        {
            Directory.Delete(model, recursive: true);
        }
    }

    private static Task<Tomix.Core.Results.TomixResult<string>> RunAsync(
        ModelReference model, MutationOptions options, MutationStores stores)
        => MutationRunner.RunAsync(
            [new TmdlModelProvider()], model, options, "set", stores,
            (_, _, _) => Task.FromResult<(bool, string, Func<MutationOutcome, string>)>(
                (true, "test mutation", _ => "mutated")),
            revertResult: "reverted",
            CancellationToken.None);

    private static string CopySample()
    {
        var dest = Path.Combine(Path.GetTempPath(), $"tomix-runner-test-{Guid.NewGuid():N}");
        CopyDirectory(LocateSample(), dest);
        return dest;
    }

    private static string LocateSample()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "samples", "basic-tmdl");
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new InvalidOperationException("samples/basic-tmdl not found above test base directory.");
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }
}
