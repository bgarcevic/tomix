using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.AppConfig;
using Microsoft.Identity.Client.Extensions.Msal;
using Tomix.Core.Authentication;
using Tomix.Platform.Configuration;

namespace Tomix.Auth;

/// <summary>
/// MSAL-backed authentication: interactive browser, device code, service principal
/// (secret/certificate), and managed identity. Tokens are cached via MSAL.Extensions
/// (DPAPI on Windows, Keychain on macOS, libsecret on Linux). Implements both the
/// <see cref="IAuthenticator"/> contract used by the <c>auth</c> command and the
/// <see cref="IAccessTokenProvider"/> contract used by the remote TOM provider.
/// </summary>
public sealed class MsalAuthenticator : IAuthenticator, IAccessTokenProvider
{
    private const string UserCacheFileName = "tomix-msal-user.cache";
    private const string AppCacheFileName = "tomix-msal-app.cache";

    private readonly MsalAuthSettings _settings;
    private readonly string _cacheDirectory;
    private readonly AuthStateStore _stateStore;
    private readonly CredentialStore _credentialStore;
    private readonly Action<string> _messageWriter;

    private IPublicClientApplication? _publicApp;
    private IConfidentialClientApplication? _confidentialApp;
    private MsalCacheHelper? _userCacheHelper;
    private MsalCacheHelper? _appCacheHelper;

    public MsalAuthenticator(
        MsalAuthSettings settings,
        string? cacheDirectory = null,
        string? stateFile = null,
        string? credentialFile = null,
        Action<string>? messageWriter = null)
    {
        _settings = settings;
        _cacheDirectory = cacheDirectory ?? TomixPaths.AuthDirectory;
        _stateStore = new AuthStateStore(stateFile ?? TomixPaths.AuthStateFile);
        _credentialStore = new CredentialStore(credentialFile);
        _messageWriter = messageWriter ?? (_ => { });
    }

    public async Task<AuthIdentity> LoginAsync(AuthLoginOptions options, CancellationToken cancellationToken)
    {
        var scopes = AuthScopes.ForEndpoint(options.TargetEndpoint);
        var result = options.Method switch
        {
            AuthMethod.Interactive => await InteractiveAsync(scopes, options, cancellationToken).ConfigureAwait(false),
            AuthMethod.DeviceCode => await DeviceCodeAsync(scopes, options, cancellationToken).ConfigureAwait(false),
            AuthMethod.ServicePrincipalSecret => await ClientCredentialsAsync(scopes, options, useCertificate: false, cancellationToken).ConfigureAwait(false),
            AuthMethod.ServicePrincipalCertificate => await ClientCredentialsAsync(scopes, options, useCertificate: true, cancellationToken).ConfigureAwait(false),
            AuthMethod.ManagedIdentity => await ManagedIdentityAsync(options.TargetEndpoint, options.ClientId, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.Method, "Unsupported auth method.")
        };

        var identity = ToIdentity(result, options);
        if (options.Save)
        {
            _stateStore.Save(new AuthState(
                options.Method,
                identity.Username,
                identity.TenantId,
                options.ClientId,
                options.TargetEndpoint,
                identity.ExpiresOn));

            if (options.Method is AuthMethod.ServicePrincipalSecret or AuthMethod.ServicePrincipalCertificate)
            {
                _credentialStore.Save(options);
                _messageWriter($"Service-principal credentials saved for silent renewal: {_credentialStore.StorageDescription}. Use --save false to skip.");
            }
        }
        return identity;
    }

    public async Task<AuthIdentity?> StatusAsync(CancellationToken cancellationToken)
    {
        var state = _stateStore.Load();
        if (state is null)
            return null;

        if (state.Method is AuthMethod.Interactive or AuthMethod.DeviceCode)
        {
            var app = await EnsurePublicAppAsync(state.TenantId).ConfigureAwait(false);
            var account = (await app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault();
            if (account is null)
            {
                // The MSAL cache was cleared independently of our sidecar; treat as signed out.
                _stateStore.Delete();
                return null;
            }

            var expiresOn = state.ExpiresOn;
            try
            {
                var refreshed = await app.AcquireTokenSilent(AuthScopes.ForEndpoint(state.Endpoint), account)
                    .ExecuteAsync(cancellationToken).ConfigureAwait(false);
                expiresOn = refreshed.ExpiresOn;
            }
            catch (MsalUiRequiredException)
            {
                // Token needs an interactive refresh; report the cached account with the stored expiry.
            }

            return new AuthIdentity(account.Username, state.TenantId, state.Method, expiresOn, StorageLabel,
                TokenAccount: account.Username, TokenTenantId: state.TenantId);
        }

        // Service principal / managed identity: no user account in the cache to refresh from.
        return new AuthIdentity(state.Username, state.TenantId, state.Method, state.ExpiresOn, StorageLabel,
            TokenAccount: state.Username, TokenTenantId: state.TenantId);
    }

    /// <summary>
    /// Username recorded at the last login, read from the sidecar state file only — never
    /// touches MSAL or the OS keystore, so it is safe to call on any code path (keystore
    /// access can block or prompt; see <see cref="GetTokenAsync"/>).
    /// </summary>
    public string? CachedUsername()
    {
        try
        {
            return _stateStore.Load()?.Username;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> LogoutAsync(CancellationToken cancellationToken)
    {
        var hadState = _stateStore.Delete();
        _credentialStore.Delete();
        var removedAccounts = false;

        try
        {
            var app = await EnsurePublicAppAsync(null).ConfigureAwait(false);
            foreach (var account in (await app.GetAccountsAsync().ConfigureAwait(false)).ToList())
            {
                await app.RemoveAsync(account).ConfigureAwait(false);
                removedAccounts = true;
            }
        }
        catch
        {
            // Cache may be absent or unreadable; nothing else to remove.
        }

        return hadState || removedAccounts;
    }

    /// <summary>
    /// Silent token acquisition never legitimately takes minutes, but the OS keystore (or the
    /// managed-identity endpoint) can block indefinitely — observed as a ~4.5-minute silent stall
    /// during workspace sync. Cap it so the failure surfaces fast with an actionable message.
    /// </summary>
    internal static readonly TimeSpan TokenAcquisitionTimeout = TimeSpan.FromSeconds(30);

    public async Task<AccessToken> GetTokenAsync(string endpoint, CancellationToken cancellationToken)
    {
        // Gate on the sidecar state before touching MSAL: with no recorded login there is no
        // token to acquire silently, and opening the keystore-backed cache just to discover
        // that can hang (macOS Keychain authorization prompts block non-interactive processes).
        var state = _stateStore.Load()
            ?? throw new AuthenticationRequiredException("Not authenticated. Run 'tx auth login'.");

        return await WithTimeoutAsync(
            AcquireTokenSilentlyAsync(endpoint, state, cancellationToken),
            TokenAcquisitionTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<AccessToken> WithTimeoutAsync(
        Task<AccessToken> acquisition,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var completed = await Task.WhenAny(acquisition, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
        if (completed != acquisition)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new AuthenticationRequiredException(
                $"Timed out acquiring an access token after {timeout.TotalSeconds:0}s "
                + "(the OS keystore or identity endpoint did not respond). Run 'tx auth login' to refresh credentials.");
        }

        return await acquisition.ConfigureAwait(false);
    }

    private async Task<AccessToken> AcquireTokenSilentlyAsync(
        string endpoint, AuthState state, CancellationToken cancellationToken)
    {
        var scopes = AuthScopes.ForEndpoint(endpoint);
        var method = state.Method;

        switch (method)
        {
            case AuthMethod.Interactive:
            case AuthMethod.DeviceCode:
                {
                    var app = await EnsurePublicAppAsync(state.TenantId).ConfigureAwait(false);
                    var account = (await app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault()
                        ?? throw new AuthenticationRequiredException("Not authenticated. Run 'tx auth login'.");
                    try
                    {
                        var result = await app.AcquireTokenSilent(scopes, account)
                            .ExecuteAsync(cancellationToken).ConfigureAwait(false);
                        return new AccessToken(result.AccessToken, result.ExpiresOn);
                    }
                    catch (MsalUiRequiredException)
                    {
                        throw new AuthenticationRequiredException("Session expired. Run 'tx auth login'.");
                    }
                }

            case AuthMethod.ServicePrincipalSecret:
            case AuthMethod.ServicePrincipalCertificate:
                {
                    if (_confidentialApp is not null)
                    {
                        var cached = await _confidentialApp.AcquireTokenForClient(scopes)
                            .ExecuteAsync(cancellationToken).ConfigureAwait(false);
                        return new AccessToken(cached.AccessToken, cached.ExpiresOn);
                    }

                    var options = _credentialStore.Load(method, endpoint)
                        ?? throw new AuthenticationRequiredException(
                            "Service-principal token expired and no saved credentials are available for silent renewal. Run 'tx auth login' again (pipe the secret via '--password -' or use --password-file).");
                    var app = await EnsureConfidentialAppAsync(options, method == AuthMethod.ServicePrincipalCertificate).ConfigureAwait(false);
                    var result = await app.AcquireTokenForClient(scopes)
                        .ExecuteAsync(cancellationToken).ConfigureAwait(false);
                    return new AccessToken(result.AccessToken, result.ExpiresOn);
                }

            case AuthMethod.ManagedIdentity:
                {
                    var result = await ManagedIdentityAsync(endpoint, state.ClientId, cancellationToken).ConfigureAwait(false);
                    return new AccessToken(result.AccessToken, result.ExpiresOn);
                }

            default:
                throw new AuthenticationRequiredException("Not authenticated. Run 'tx auth login'.");
        }
    }

    private async Task<AuthenticationResult> InteractiveAsync(
        string[] scopes,
        AuthLoginOptions options,
        CancellationToken cancellationToken)
    {
        var app = await EnsurePublicAppAsync(options.Tenant).ConfigureAwait(false);
        var builder = app.AcquireTokenInteractive(scopes).WithUseEmbeddedWebView(false);

        var existing = (await app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault();
        if (existing is not null)
            builder = builder.WithAccount(existing);

        return await builder.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<AuthenticationResult> DeviceCodeAsync(
        string[] scopes,
        AuthLoginOptions options,
        CancellationToken cancellationToken)
    {
        var app = await EnsurePublicAppAsync(options.Tenant).ConfigureAwait(false);
        return await app.AcquireTokenWithDeviceCode(scopes, deviceCode =>
            {
                _messageWriter(deviceCode.Message);
                return Task.CompletedTask;
            })
            .ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<AuthenticationResult> ClientCredentialsAsync(
        string[] scopes,
        AuthLoginOptions options,
        bool useCertificate,
        CancellationToken cancellationToken)
    {
        var app = await EnsureConfidentialAppAsync(options, useCertificate).ConfigureAwait(false);
        return await app.AcquireTokenForClient(scopes).ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AuthenticationResult> ManagedIdentityAsync(string? endpoint, string? clientId, CancellationToken cancellationToken)
    {
        var identityId = string.IsNullOrWhiteSpace(clientId)
            ? ManagedIdentityId.SystemAssigned
            : ManagedIdentityId.WithUserAssignedClientId(clientId);
        var app = ManagedIdentityApplicationBuilder.Create(identityId).Build();
        return await app.AcquireTokenForManagedIdentity(AuthScopes.ResourceForEndpoint(endpoint))
            .ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IConfidentialClientApplication> EnsureConfidentialAppAsync(
        AuthLoginOptions options, bool useCertificate)
    {
        if (_confidentialApp is not null)
            return _confidentialApp;

        if (string.IsNullOrWhiteSpace(options.ClientId))
            throw new AuthenticationRequiredException("Service-principal sign-in requires a client id (--username/-u).");

        if (string.IsNullOrWhiteSpace(options.Tenant))
            throw new AuthenticationRequiredException("Service-principal sign-in requires a tenant (--tenant/-t).");

        var builder = ConfidentialClientApplicationBuilder
            .Create(options.ClientId)
            .WithAuthority($"https://login.microsoftonline.com/{options.Tenant}");

        if (useCertificate)
        {
            if (string.IsNullOrWhiteSpace(options.CertificatePath))
                throw new AuthenticationRequiredException("Certificate sign-in requires --certificate <path>.");

            var certificate = LoadCertificate(options.CertificatePath, options.CertificatePassword);
            builder = builder.WithCertificate(certificate);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(options.ClientSecret))
                throw new AuthenticationRequiredException(
                    "Service-principal sign-in requires a client secret (pipe it via '--password -' or use --password-file).");

            builder = builder.WithClientSecret(options.ClientSecret);
        }

        var app = builder.Build();

        var helper = await GetAppCacheHelperAsync().ConfigureAwait(false);
        helper.RegisterCache(app.AppTokenCache);

        _confidentialApp = app;
        return app;
    }

    private static X509Certificate2 LoadCertificate(string path, string? password)
    {
        if (path.EndsWith(".pem", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrEmpty(password)
                ? X509Certificate2.CreateFromPemFile(path)
                : X509Certificate2.CreateFromEncryptedPemFile(path, password.AsSpan());

        return X509CertificateLoader.LoadPkcs12FromFile(path, password);
    }

    private async Task<IPublicClientApplication> EnsurePublicAppAsync(string? tenant)
    {
        if (_publicApp is not null)
            return _publicApp;

        var app = PublicClientApplicationBuilder
            .Create(_settings.ClientId)
            .WithAuthority(ResolveAuthority(tenant))
            .WithRedirectUri("http://localhost")
            .Build();

        var helper = await GetUserCacheHelperAsync().ConfigureAwait(false);
        helper.RegisterCache(app.UserTokenCache);

        _publicApp = app;
        return app;
    }

    private async Task<MsalCacheHelper> GetUserCacheHelperAsync()
    {
        if (_userCacheHelper is not null)
            return _userCacheHelper;
        _userCacheHelper = await CreateCacheHelperAsync(UserCacheFileName).ConfigureAwait(false);
        return _userCacheHelper;
    }

    private async Task<MsalCacheHelper> GetAppCacheHelperAsync()
    {
        if (_appCacheHelper is not null)
            return _appCacheHelper;
        _appCacheHelper = await CreateCacheHelperAsync(AppCacheFileName).ConfigureAwait(false);
        return _appCacheHelper;
    }

    private async Task<MsalCacheHelper> CreateCacheHelperAsync(string fileName)
    {
        var storage = new StorageCreationPropertiesBuilder(fileName, _cacheDirectory)
            .WithLinuxKeyring(
                schemaName: "com.Tomix.tokencache",
                collection: MsalCacheHelper.LinuxKeyRingDefaultCollection,
                secretLabel: "tomix token cache",
                attribute1: new KeyValuePair<string, string>("Version", "1"),
                attribute2: new KeyValuePair<string, string>("Product", "tomix"))
            .WithMacKeyChain(serviceName: "tomix-msal-cache", accountName: "TomixCache")
            .Build();

        return await MsalCacheHelper.CreateAsync(storage).ConfigureAwait(false);
    }

    private string ResolveAuthority(string? tenant)
        => string.IsNullOrWhiteSpace(tenant)
            ? _settings.Authority
            : $"https://login.microsoftonline.com/{tenant}";

    private AuthIdentity ToIdentity(AuthenticationResult result, AuthLoginOptions options)
    {
        var username = result.Account?.Username ?? DefaultUsername(options);
        var tenant = result.TenantId
            ?? options.Tenant
            ?? result.Account?.HomeAccountId?.TenantId;

        var tokenAccount = result.Account?.Username;
        var tokenTenant = result.TenantId ?? result.Account?.HomeAccountId?.TenantId;

        return new AuthIdentity(username, tenant, options.Method, result.ExpiresOn, StorageLabel,
            TokenAccount: tokenAccount, TokenTenantId: tokenTenant);
    }

    private static string DefaultUsername(AuthLoginOptions options)
        => options.Method == AuthMethod.ManagedIdentity
            ? "managed-identity"
            : string.IsNullOrWhiteSpace(options.ClientId) ? "service-principal" : $"app:{options.ClientId}";

    private static string StorageLabel =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "OS keystore (DPAPI)"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "OS keystore (Keychain)"
        : "OS keystore (libsecret)";
}
