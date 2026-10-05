using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Tomix.Ui;

/// <summary>What <see cref="UiHost"/> serves and how it reaches the session.</summary>
public sealed record UiHostOptions
{
    /// <summary>The port on <c>127.0.0.1</c>; <c>0</c> picks a free one.</summary>
    public int Port { get; init; }

    /// <summary>The secret every request must carry (<see cref="UiHost.NewToken"/>).</summary>
    public required string Token { get; init; }

    /// <summary>The body of <c>GET /status</c>: a JSON object, read on every request.</summary>
    public required Func<string> Status { get; init; }

    /// <summary>Serves one protocol client on an accepted WebSocket until it closes.</summary>
    public required Func<WebSocket, CancellationToken, Task> Connect { get; init; }
}

/// <summary>
/// The localhost web endpoint of a shared live session (docs/protocol.md, Transport):
/// <c>/ws</c> carries the protocol over a WebSocket and <c>GET /status</c> answers a small JSON
/// status for tools that poll. It listens on <c>127.0.0.1</c> only, and every request must carry
/// the session token, name this host in <c>Host</c>, and come from no other web origin.
/// </summary>
public sealed class UiHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private UiHost(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    /// <summary>The port the host listens on.</summary>
    public int Port { get; }

    /// <summary><c>http://127.0.0.1:&lt;port&gt;/</c>.</summary>
    public Uri BaseUrl => new($"http://127.0.0.1:{Port}/");

    /// <summary>The application, for tests that drive it through a test server.</summary>
    internal WebApplication App => _app;

    /// <summary>A new random session token: 32 bytes, base64url.</summary>
    public static string NewToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Starts listening.</summary>
    /// <exception cref="IOException">The port is taken.</exception>
    public static Task<UiHost> StartAsync(UiHostOptions options, CancellationToken cancellationToken)
        => StartAsync(options, configure: null, cancellationToken);

    /// <summary>Starts listening, with the web host adjusted first.</summary>
    /// <param name="configure">Replaces the server, for tests (<c>UseTestServer</c>).</param>
    public static async Task<UiHost> StartAsync(UiHostOptions options, Action<IWebHostBuilder>? configure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(UiHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory
        });

        // stdout may carry the protocol, and Ctrl+C belongs to the command that started the host.
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHostLifetime, PassiveLifetime>();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, options.Port));
        configure?.Invoke(builder.WebHost);

        var app = builder.Build();
        var port = options.Port;
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.Run(context => HandleAsync(context, options, port));
        await app.StartAsync(cancellationToken);

        if (port == 0 && app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>() is { } addresses)
            port = addresses.Addresses.Select(address => new Uri(address).Port).FirstOrDefault();
        return new UiHost(app, port);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
    }

    private static async Task HandleAsync(HttpContext context, UiHostOptions options, int port)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";

        // A page on another site can make the browser send requests here; these checks keep it out.
        if (!IsLocalHost(request.Host, port))
        {
            await RefuseAsync(response, StatusCodes.Status403Forbidden, "TOMIX_UI_FORBIDDEN", "The Host header does not name this server.");
            return;
        }

        if (request.Headers.Origin is { Count: > 0 } origin && !IsOwnOrigin(origin.ToString(), port))
        {
            await RefuseAsync(response, StatusCodes.Status403Forbidden, "TOMIX_UI_FORBIDDEN", "Requests from other web origins are not accepted.");
            return;
        }

        if (!HasToken(request, options.Token))
        {
            await RefuseAsync(response, StatusCodes.Status401Unauthorized, "TOMIX_UI_UNAUTHORIZED", "The session token is missing or wrong.");
            return;
        }

        switch (request.Path.Value)
        {
            case "/status" when HttpMethods.IsGet(request.Method):
                response.ContentType = "application/json; charset=utf-8";
                await response.WriteAsync(options.Status(), context.RequestAborted);
                return;
            case "/ws" when context.WebSockets.IsWebSocketRequest:
                using (var socket = await context.WebSockets.AcceptWebSocketAsync())
                    await options.Connect(socket, context.RequestAborted);
                return;
            case "/ws":
                await RefuseAsync(response, StatusCodes.Status400BadRequest, "TOMIX_UI_NOT_WEBSOCKET", "/ws takes WebSocket upgrade requests only.");
                return;
            default:
                await RefuseAsync(response, StatusCodes.Status404NotFound, "TOMIX_UI_NOT_FOUND", $"Nothing is served at {request.Path}.");
                return;
        }
    }

    /// <summary>Only <c>127.0.0.1</c> or <c>localhost</c> on this port: refuses DNS rebinding.</summary>
    private static bool IsLocalHost(HostString host, int port)
        => host.HasValue
           && (host.Host == "127.0.0.1" || host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
           && (host.Port ?? 80) == port;

    private static bool IsOwnOrigin(string origin, int port)
        => Uri.TryCreate(origin, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttp
           && IsLocalHost(new HostString(uri.Host, uri.Port), port);

    /// <summary><c>Authorization: Bearer &lt;token&gt;</c>, or <c>?token=</c> for browsers, which cannot set headers on a WebSocket.</summary>
    private static bool HasToken(HttpRequest request, string token)
    {
        var given = request.Headers.Authorization.ToString() is { Length: > 7 } header && header.StartsWith("Bearer ", StringComparison.Ordinal)
            ? header[7..]
            : request.Query["token"].ToString();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(token));
    }

    private static Task RefuseAsync(HttpResponse response, int status, string code, string message)
    {
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        var encoder = JavaScriptEncoder.Default;
        return response.WriteAsync($"{{\"code\":\"{code}\",\"error\":\"{encoder.Encode(message)}\"}}");
    }

    /// <summary>Starts and stops with the host and nothing else: no Ctrl+C handling, no console messages.</summary>
    private sealed class PassiveLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
