using Tomix.Core.Models;

namespace Tomix.App.Models;

/// <summary>
/// App-side provider resolution: Core's <see cref="ModelProviderResolver.ResolveSingle"/> plus a
/// filesystem probe that distinguishes "nothing owns this reference" from "the source exists but
/// cannot be read". <see cref="IModelProvider.CanOpen"/> is a total predicate — providers treat an
/// unreadable candidate as unowned — so without the probe a permission error on an existing model
/// source would surface as the misleading no-provider diagnostic instead of naming the file. The
/// probe does real I/O, so it lives here rather than in the infrastructure-free Core resolver;
/// application code resolves through this method, never through Core's directly.
/// </summary>
public static class ModelProviderResolution
{
    /// <summary>
    /// Resolves like <see cref="ModelProviderResolver.ResolveSingle"/> (null when no provider
    /// matches, <see cref="AmbiguousModelProviderException"/> when several do), but a local
    /// reference that exists on disk and cannot be read throws <see cref="ModelLoadException"/>
    /// naming the path instead of returning null.
    /// </summary>
    public static IModelProvider? ResolveSingleProvider(
        this IEnumerable<IModelProvider> providers, ModelReference model)
    {
        var match = providers.ResolveSingle(model);
        if (match is null)
            ThrowIfUnreadableSource(model);

        return match;
    }

    private static void ThrowIfUnreadableSource(ModelReference model)
    {
        if (!model.IsLocalPath)
            return;

        try
        {
            // Probe by opening, not by Exists-gating: File/Directory.Exists report false for a
            // path the process cannot traverse to, which would silently skip the probe and let
            // an access-denied source fall through to the no-provider diagnostic.
            if (Directory.Exists(model.Value))
                _ = Directory.EnumerateFileSystemEntries(model.Value).FirstOrDefault();
            else
                File.OpenRead(model.Value).Dispose();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException
            or ArgumentException or NotSupportedException)
        {
            // The source genuinely is not there, or the value is not a probeable path shape
            // at all; both are the callers' no-provider case, not an unreadable source.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModelLoadException(
                $"Cannot read model source '{model.Value}': {ex.Message}", ex);
        }
    }
}
