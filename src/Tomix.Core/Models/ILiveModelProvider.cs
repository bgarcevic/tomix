namespace Tomix.Core.Models;

/// <summary>
/// A provider that can also hold a model open as an <see cref="ILiveModelSession"/> (ADR 0001).
/// Providers implement it next to <see cref="IModelProvider"/>, for the same references.
/// </summary>
public interface ILiveModelProvider
{
    /// <summary>
    /// Loads the model and opens a live session over it. Unlike
    /// <see cref="IModelProvider.OpenAsync"/>, the model is loaded before this returns, so an
    /// unreadable source fails here with a <see cref="ModelLoadException"/>.
    /// </summary>
    /// <param name="reference">A reference this provider's <see cref="IModelProvider.CanOpen"/> accepts.</param>
    /// <param name="cancellationToken">A token that can cancel the open operation.</param>
    /// <returns>The session. The caller owns it and closes it by disposing it.</returns>
    Task<ILiveModelSession> OpenLiveAsync(ModelReference reference, CancellationToken cancellationToken);
}
