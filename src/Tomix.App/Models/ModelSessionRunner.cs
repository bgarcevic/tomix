using Tomix.App.Diagnostics;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Models;

/// <summary>
/// Owns the session lifecycle shared by application handlers that operate on one model: take a
/// session from an <see cref="IModelSessionSource"/>, map connection errors, and end the lease.
/// A successful action is committed, so it costs nothing on a one-shot session and keeps its
/// effects on a live one; a failed or throwing action rolls back.
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
        => await RunAsync(new OneShotSessionSource(providers), model, action, cancellationToken);

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
        => await RunAsync(
            new OneShotSessionSource(providers, noProviderMessage, noProviderHint), model, action, cancellationToken);

    public static async Task<TomixResult<TResult>> RunAsync<TResult>(
        IModelSessionSource sessions,
        ModelReference model,
        Func<IModelSession, Task<TomixResult<TResult>>> action,
        CancellationToken cancellationToken)
        => await ProviderConnectionGuard.RunAsync(model, async () =>
        {
            ModelSessionLease lease;
            try
            {
                lease = await sessions.LeaseAsync(model, cancellationToken);
            }
            catch (ModelSessionUnavailableException ex)
            {
                return Unavailable<TResult>(ex);
            }

            await using (lease)
            {
                var result = await action(lease.Session);
                if (result.Success)
                    await lease.CommitAsync(cancellationToken);
                return result;
            }
        });

    internal static TomixResult<TResult> Unavailable<TResult>(ModelSessionUnavailableException ex)
        => TomixResult<TResult>.Fail(ex.Code, ex.Message, ex.ExitCode, ex.Hint);
}
