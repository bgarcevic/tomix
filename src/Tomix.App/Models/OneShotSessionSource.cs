using Tomix.Core.Models;

namespace Tomix.App.Models;

/// <summary>The one-shot CLI: resolve the provider, open the model, and dispose it when the lease ends.</summary>
public sealed class OneShotSessionSource : IModelSessionSource
{
    private readonly string? _noProviderMessage;
    private readonly string? _noProviderHint;

    public OneShotSessionSource(
        IEnumerable<IModelProvider> providers,
        string? noProviderMessage = null,
        string? noProviderHint = ModelSessionRunner.DefaultNoProviderHint)
    {
        Providers = providers as IReadOnlyList<IModelProvider> ?? providers.ToList();
        _noProviderMessage = noProviderMessage;
        _noProviderHint = noProviderHint;
    }

    /// <summary>The registered providers; staging materializes its working copy with them.</summary>
    public IReadOnlyList<IModelProvider> Providers { get; }

    public bool IsLive => false;

    public async Task<ModelSessionLease> LeaseAsync(ModelReference model, CancellationToken cancellationToken)
    {
        var provider = Providers.ResolveSingleProvider(model)
            ?? throw new ModelSessionUnavailableException(
                "TOMIX_NO_PROVIDER",
                _noProviderMessage ?? $"No provider can open model: {model.Value}",
                exitCode: 2,
                _noProviderHint);

        return ModelSessionLease.OneShot(await provider.OpenAsync(model, cancellationToken));
    }
}
