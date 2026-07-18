namespace Tomix.Core.Models;

/// <summary>
/// Thrown when more than one registered provider claims the same model reference. Provider
/// <see cref="IModelProvider.CanOpen"/> contracts are meant to be mutually exclusive, so an
/// overlap is a provider-registration bug that must surface as a diagnostic instead of being
/// resolved silently by registration order.
/// </summary>
public sealed class AmbiguousModelProviderException : Exception
{
    public AmbiguousModelProviderException(ModelReference model, IReadOnlyList<string> providerNames)
        : base($"Multiple providers claim model '{model.Value}': {string.Join(", ", providerNames)}. "
            + "This is a provider-registration bug, not a problem with the model.")
    {
    }
}

/// <summary>
/// Owns provider selection: exactly one provider may claim a model reference. Returns null
/// when none match (callers report their command-specific no-provider diagnostic) and throws
/// <see cref="AmbiguousModelProviderException"/> when several do.
/// </summary>
public static class ModelProviderResolver
{
    public static IModelProvider? ResolveSingle(
        this IEnumerable<IModelProvider> providers, ModelReference model)
    {
        IModelProvider? match = null;
        List<string>? claimants = null;
        foreach (var provider in providers)
        {
            if (!provider.CanOpen(model))
                continue;

            if (match is null)
            {
                match = provider;
                continue;
            }

            claimants ??= [match.GetType().Name];
            claimants.Add(provider.GetType().Name);
        }

        if (claimants is not null)
            throw new AmbiguousModelProviderException(model, claimants);

        return match;
    }
}
