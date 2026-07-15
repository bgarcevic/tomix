using Tomix.Core.Models;
using Tomix.Provider.Tmdl;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// A corrupt model source must surface as the provider-agnostic <see cref="ModelLoadException"/>,
/// not the underlying serializer's exception type.
/// </summary>
public sealed class ModelLoadExceptionTests
{
    [Fact]
    public async Task TmdlSession_WrapsDeserializationFailure()
    {
        var folder = Directory.CreateTempSubdirectory("tomix-broken-tmdl").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "model.tmdl"), "this is not valid tmdl {");

            await using var session = new TmdlModelSession(folder);
            var ex = await Assert.ThrowsAsync<ModelLoadException>(
                () => session.GetSnapshotAsync(CancellationToken.None));

            Assert.Contains(folder, ex.Message);
            Assert.NotNull(ex.InnerException);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task BimSession_WrapsDeserializationFailure()
    {
        var file = Path.Combine(Path.GetTempPath(), $"tomix-broken-{Guid.NewGuid():N}.bim");
        try
        {
            File.WriteAllText(file, "{ not valid bim json");

            var provider = new TomFileModelProvider();
            await using var session = await provider.OpenAsync(new ModelReference(file), CancellationToken.None);
            var ex = await Assert.ThrowsAsync<ModelLoadException>(
                () => session.GetSnapshotAsync(CancellationToken.None));

            Assert.Contains(file, ex.Message);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
