namespace Tomix.Core.Models;

/// <summary>
/// Recognizes model references and opens sessions for the models it owns.
/// </summary>
public interface IModelProvider
{
    /// <summary>
    /// Determines whether this provider recognizes <paramref name="reference"/>.
    /// </summary>
    /// <remarks>
    /// Callers may use this method during command-line argument disambiguation as well as provider
    /// resolution. Implementations must therefore be cheap, synchronous, and side-effect-free. They
    /// may inspect local filesystem metadata, but must not access the network, authenticate, deserialize
    /// a model, or otherwise open the model.
    /// </remarks>
    /// <param name="reference">The model reference to classify.</param>
    /// <returns><see langword="true"/> when this provider owns the reference; otherwise, <see langword="false"/>.</returns>
    bool CanOpen(ModelReference reference);

    /// <summary>
    /// Opens a session for a model reference owned by this provider.
    /// </summary>
    /// <param name="reference">The model reference to open.</param>
    /// <param name="cancellationToken">A token that can cancel the open operation.</param>
    /// <returns>A task whose result owns the opened model session.</returns>
    Task<IModelSession> OpenAsync(
        ModelReference reference,
        CancellationToken cancellationToken);
}
