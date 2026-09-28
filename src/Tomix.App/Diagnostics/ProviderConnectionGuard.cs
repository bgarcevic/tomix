using Tomix.Core.Authentication;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Diagnostics;

/// <summary>
/// Converts provider connection failures into the uniform diagnostics every model-opening
/// handler must emit (<c>TOMIX_DATABASE_NOT_FOUND</c>, <c>TOMIX_DATABASE_REQUIRED</c>, <c>TOMIX_AUTH_REQUIRED</c>,
/// <c>TOMIX_CONNECT_FAILED</c>). Handlers wrap their open-and-work body in
/// <see cref="RunAsync{T}"/> instead of copying these catch blocks; handler-specific
/// exceptions should be caught inside the body so they keep their own diagnostics.
/// </summary>
public static class ProviderConnectionGuard
{
    /// <param name="model">The model being opened; null (an optional model that was not supplied)
    /// behaves like a local reference, so only authentication failures are converted.</param>
    public static async Task<TomixResult<T>> RunAsync<T>(
        ModelReference? model,
        Func<Task<TomixResult<T>>> action)
    {
        try
        {
            return await action();
        }
        catch (ModelConnectionException ex)
        {
            return ConnectionFailure<T>(model, ex);
        }
        catch (AuthenticationRequiredException ex)
        {
            return TomixResult<T>.Fail("TOMIX_AUTH_REQUIRED", ex.Message, exitCode: 1,
                hint: "Run 'tx auth login' to authenticate, or use --auth spn for service principal.");
        }
        catch (Exception ex) when (model is { IsRemote: true } && ex is not OperationCanceledException)
        {
            return TomixResult<T>.Fail(
                "TOMIX_CONNECT_FAILED",
                RemoteConnectError.Describe(model.Value, ex),
                exitCode: 1,
                hint: "Verify the server URL and credentials.");
        }
    }

    /// <summary>
    /// Maps a classified <see cref="ModelConnectionException"/> to its diagnostic. Public for
    /// handlers that open sessions outside <see cref="RunAsync{T}"/> (e.g. <c>query</c>).
    /// </summary>
    public static TomixResult<T> ConnectionFailure<T>(ModelReference? model, ModelConnectionException ex)
        => ex.Kind switch
        {
            ModelConnectionFailureKind.DatabaseRequired => TomixResult<T>.Fail(
                "TOMIX_DATABASE_REQUIRED",
                ex.Message,
                exitCode: 2,
                hint: model is null
                    ? "Name the model with --database."
                    : $"List the models on the endpoint: tx connect \"{model.Value}\" --list"),
            _ => TomixResult<T>.Fail("TOMIX_DATABASE_NOT_FOUND", ex.Message, exitCode: 1)
        };
}
