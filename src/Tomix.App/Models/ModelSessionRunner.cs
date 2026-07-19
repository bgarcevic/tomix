using Tomix.App.Diagnostics;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Models;

/// <summary>
/// Owns the provider-resolution, connection-error mapping, session-opening, and disposal
/// lifecycle shared by application handlers that operate on one model.
/// </summary>
public static class ModelSessionRunner
{
    public const string DefaultNoProviderHint =
        "Supported formats: TMDL folder, .bim file. For remote models, use --server and --database.";

    public static async Task<TomixResult<TResult>> RunAsync<TResult>(
        IReadOnlyList<IModelProvider> providers,
        ModelReference model,
        Func<IModelSession, Task<TomixResult<TResult>>> action,
        CancellationToken cancellationToken)
        => await RunAsync(
            providers, model, action, noProviderMessage: null, DefaultNoProviderHint, cancellationToken);

    public static async Task<TomixResult<TResult>> RunAsync<TResult>(
        IReadOnlyList<IModelProvider> providers,
        ModelReference model,
        Func<IModelSession, TomixResult<TResult>> action,
        CancellationToken cancellationToken)
        => await RunAsync(
            providers, model, session => Task.FromResult(action(session)), cancellationToken);

    public static async Task<TomixResult<TResult>> RunAsync<TResult>(
        IReadOnlyList<IModelProvider> providers,
        ModelReference model,
        Func<IModelSession, Task<TomixResult<TResult>>> action,
        string? noProviderMessage,
        string? noProviderHint,
        CancellationToken cancellationToken)
    {
        var provider = providers.ResolveSingle(model);
        if (provider is null)
            return TomixResult<TResult>.Fail(
                "TOMIX_NO_PROVIDER",
                noProviderMessage ?? $"No provider can open model: {model.Value}",
                exitCode: 2,
                hint: noProviderHint);

        return await ProviderConnectionGuard.RunAsync(model, async () =>
        {
            await using var session = await provider.OpenAsync(model, cancellationToken);
            return await action(session);
        });
    }
}
