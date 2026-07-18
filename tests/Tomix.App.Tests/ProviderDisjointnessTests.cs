using Tomix.Core.Models;
using Tomix.Provider.Tmdl;
using Tomix.Provider.Tom;

namespace Tomix.App.Tests;

/// <summary>
/// Pins the mutual exclusivity of the registered providers' CanOpen contracts: for each
/// supported model-reference shape, exactly one provider claims it, so ResolveSingle never
/// hits its ambiguity path with the real provider set.
/// </summary>
public sealed class ProviderDisjointnessTests
{
    private static IModelProvider[] RealProviders() =>
    [
        new TmdlModelProvider(),
        new TomFileModelProvider(),
        new TomServerModelProvider(tokenProvider: null),
    ];

    [Fact]
    public void TmdlFolder_IsClaimedOnlyByTmdlProvider()
    {
        var provider = RealProviders().ResolveSingle(new ModelReference(LocateSample()));

        Assert.IsType<TmdlModelProvider>(provider);
    }

    [Fact]
    public void BimFile_IsClaimedOnlyByTomFileProvider()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tomix-disjoint-{Guid.NewGuid():N}.bim");
        File.WriteAllText(path, "{}");
        try
        {
            var provider = RealProviders().ResolveSingle(new ModelReference(path));

            Assert.IsType<TomFileModelProvider>(provider);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RemoteReference_IsClaimedOnlyByTomServerProvider()
    {
        var reference = new ModelReference("powerbi://api.powerbi.com/v1.0/myorg/Workspace", "Model");

        var provider = RealProviders().ResolveSingle(reference);

        Assert.IsType<TomServerModelProvider>(provider);
    }

    [Fact]
    public void NonexistentPath_IsClaimedByNoProvider()
    {
        var reference = new ModelReference(Path.Combine(Path.GetTempPath(), $"tomix-missing-{Guid.NewGuid():N}"));

        Assert.Null(RealProviders().ResolveSingle(reference));
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
}
