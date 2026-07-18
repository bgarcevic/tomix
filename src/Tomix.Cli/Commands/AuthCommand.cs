using System.CommandLine;
using Tomix.App.Auth;
using Tomix.App.State;
using Tomix.Auth;
using Tomix.Cli.Output;
using Tomix.Core.Authentication;
using Spectre.Console;

namespace Tomix.Cli.Commands;

/// <summary>
/// <c>auth login | status | logout</c> — browser/device-code/service-principal sign-in to
/// Power BI, Fabric, or Azure Analysis Services, caching the token in the OS keystore.
/// </summary>
internal sealed class AuthCommand : ICommandModule
{
    public Command Build()
    {
        var command = new Command("auth", "Manage authentication for remote workspaces");
        command.Subcommands.Add(BuildLogin());
        command.Subcommands.Add(BuildLogout());
        command.Subcommands.Add(BuildStatus());
        return command;
    }

    private static Command BuildLogin()
    {
        var usernameOption = new Option<string?>("--username") { Description = "Service-principal application (client) id" };
        usernameOption.Aliases.Add("-u");
        var passwordOption = new Option<string?>("--password") { Description = "Service-principal client secret (reads from AZURE_CLIENT_SECRET env var; pass '-' to read from stdin)" };
        passwordOption.Aliases.Add("-p");
        var tenantOption = new Option<string?>("--tenant") { Description = "Tenant id or domain (required for service principal)" };
        tenantOption.Aliases.Add("-t");
        var identityOption = new Option<bool>("--identity") { Description = "Sign in with a managed identity (Azure-hosted; use --username for user-assigned)" };
        identityOption.Aliases.Add("-I");
        var certificateOption = new Option<string?>("--certificate") { Description = "Path to certificate file (PEM or PKCS12) for service principal auth" };
        var certificatePasswordOption = new Option<string?>("--certificate-password") { Description = "Password for the certificate file" };
        var deviceCodeOption = new Option<bool>("--device-code") { Description = "Use the device-code flow instead of a local browser" };
        var clientIdOption = new Option<string?>("--client-id") { Description = "Override the Azure AD client id used for interactive/device-code sign-in" };
        var saveOption = new Option<bool?>("--save") { Description = "Persist service principal credentials for silent reuse (default: true). Use --save false for one-shot login." };
        var command = new Command("login", "Log in to a Power BI / Fabric / Azure AS account")
        {
            usernameOption,
            passwordOption,
            tenantOption,
            identityOption,
            certificateOption,
            certificatePasswordOption,
            deviceCodeOption,
            clientIdOption,
            saveOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, format, "auth login", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var username = parseResult.GetValue(usernameOption);
            var password = parseResult.GetValue(passwordOption)
                ?? Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET")
                ?? Environment.GetEnvironmentVariable("TOMIX_AUTH_CLIENT_SECRET");
            if (password == "-")
                password = Console.In.ReadLine();
            var tenant = parseResult.GetValue(tenantOption);
            var certificate = parseResult.GetValue(certificateOption);
            var useIdentity = parseResult.GetValue(identityOption);
            var useDeviceCode = parseResult.GetValue(deviceCodeOption);

            var method = ResolveMethod(useIdentity, certificate, username, password, useDeviceCode);
            var endpoint = parseResult.GetValue(GlobalOptions.Server)
                ?? new CliStateStore().LoadCurrentSession()?.Server;

            var options = new AuthLoginOptions(
                method,
                TargetEndpoint: endpoint,
                Tenant: tenant,
                ClientId: IsServicePrincipal(method) || method == AuthMethod.ManagedIdentity ? username : null,
                ClientSecret: password,
                CertificatePath: certificate,
                CertificatePassword: parseResult.GetValue(certificatePasswordOption),
                Save: parseResult.GetValue(saveOption) ?? true);

            var authenticator = CreateAuthenticator(parseResult.GetValue(clientIdOption), tenant);
            var handler = new AuthHandler(authenticator);

            if (method == AuthMethod.Interactive && !OutputFormats.IsJson(format))
                Console.Error.WriteLine("Opening browser for authentication...");

            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var result = await CliSpinner.RunAsync(
                "Authenticating...",
                () => handler.LoginAsync(options, cancellationToken),
                suppress: quiet || OutputFormats.IsJson(format));
            return CommandOutput.Render(result, format, RenderLogin, data => data.Identity);
        });

        return command;
    }

    private static Command BuildStatus()
    {
        var command = new Command("status", "Show authentication status");
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, format, "auth status", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var handler = new AuthHandler(CreateAuthenticator(clientIdOverride: null, tenant: null));
            var result = await handler.StatusAsync(cancellationToken);
            return CommandOutput.Render(result, format, RenderStatus, data => FlatAuthStatus.FromResult(data));
        });
        return command;
    }

    private static Command BuildLogout()
    {
        var command = new Command("logout", "Clear cached authentication credentials");
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, format, "auth logout", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var handler = new AuthHandler(CreateAuthenticator(clientIdOverride: null, tenant: null));
            var result = await handler.LogoutAsync(cancellationToken);
            return CommandOutput.Render(
                result,
                format,
                data => AnsiConsole.MarkupLine(data.Existed ? Styling.Success("Logged out -- cached credentials cleared.") : Styling.Muted("Not logged in.")));
        });
        return command;
    }

    internal static AuthMethod ResolveMethod(bool useIdentity, string? certificate, string? username, string? password, bool useDeviceCode)
    {
        if (useIdentity)
            return AuthMethod.ManagedIdentity;
        if (!string.IsNullOrWhiteSpace(certificate))
            return AuthMethod.ServicePrincipalCertificate;
        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
            return AuthMethod.ServicePrincipalSecret;
        return useDeviceCode ? AuthMethod.DeviceCode : AuthMethod.Interactive;
    }

    private static bool IsServicePrincipal(AuthMethod method)
        => method is AuthMethod.ServicePrincipalSecret or AuthMethod.ServicePrincipalCertificate;

    private static MsalAuthenticator CreateAuthenticator(string? clientIdOverride, string? tenant)
        => new(App.Auth.AuthSettingsFactory.Resolve(clientIdOverride, tenant), messageWriter: Console.Error.WriteLine);

    private static void RenderLogin(AuthLoginResult result)
    {
        AnsiConsole.MarkupLine(Styling.Success("Authenticated"));
        RenderIdentity(result.Identity, includeMethodAndStorage: false);
    }

    private static void RenderStatus(AuthStatusResult result)
    {
        if (!result.LoggedIn || result.Identity is null)
        {
            AnsiConsole.MarkupLine(Styling.Warning("Not logged in"));
            AnsiConsole.MarkupLine(Styling.Guidance("Run 'tx auth login' to authenticate."));
            return;
        }

        AnsiConsole.MarkupLine(Styling.Success("Logged in"));
        RenderIdentity(result.Identity, includeMethodAndStorage: true);
    }

    private static void RenderIdentity(AuthIdentity identity, bool includeMethodAndStorage)
    {
        AnsiConsole.MarkupLine(Styling.KeyValue("  Account:", identity.Username ?? ""));
        AnsiConsole.MarkupLine(Styling.KeyValue("  Tenant:", identity.TenantId ?? ""));
        if (includeMethodAndStorage)
        {
            AnsiConsole.MarkupLine(Styling.KeyValue("  Method:", MethodLabel(identity.Method)));
            AnsiConsole.MarkupLine(Styling.KeyValue("  Storage:", identity.Storage ?? ""));
        }

        if (identity.ExpiresOn is { } expires)
            AnsiConsole.MarkupLine(Styling.KeyValue("  Expires:", $"{expires.ToLocalTime():yyyy-MM-dd HH:mm:ss}"));
    }

    private static string MethodLabel(AuthMethod method) => method switch
    {
        AuthMethod.Interactive => "Interactive",
        AuthMethod.DeviceCode => "Device code",
        AuthMethod.ServicePrincipalSecret => "Service principal",
        AuthMethod.ServicePrincipalCertificate => "Service principal (certificate)",
        AuthMethod.ManagedIdentity => "Managed identity",
        _ => method.ToString()
    };

    private sealed record FlatAuthStatus(
        bool Authenticated,
        bool Expired,
        string Method,
        string? Account,
        string? TenantId,
        bool EncryptedAtRest,
        string KeyStoreMode,
        DateTimeOffset? ExpiresOn,
        string? TokenAccount,
        string? TokenTenantId,
        bool TenantMismatch,
        bool UsernameMismatch)
    {
        public static FlatAuthStatus FromResult(AuthStatusResult result)
        {
            var identity = result.Identity;
            var expired = identity?.ExpiresOn is { } exp && exp <= DateTimeOffset.UtcNow;

            string[] osKeyStoreBackends = ["DPAPI", "Keychain", "libsecret"];
            var (encryptedAtRest, keyStoreMode) =
                identity?.Storage is { } storage
                && osKeyStoreBackends.Any(backend => storage.Contains(backend, StringComparison.OrdinalIgnoreCase))
                    ? (true, "OsKeyStore")
                    : (false, "None");

            var tokenAccount = identity?.TokenAccount;
            var tokenTenant = identity?.TokenTenantId;
            var tenantMismatch = tokenTenant is not null
                && identity?.TenantId is not null
                && !string.Equals(tokenTenant, identity.TenantId, StringComparison.OrdinalIgnoreCase);
            var usernameMismatch = tokenAccount is not null
                && identity?.Username is not null
                && !string.Equals(tokenAccount, identity.Username, StringComparison.OrdinalIgnoreCase);

            return new FlatAuthStatus(
                Authenticated: result.LoggedIn && !expired,
                Expired: expired,
                Method: identity?.Method.ToString() ?? "None",
                Account: identity?.Username,
                TenantId: identity?.TenantId,
                EncryptedAtRest: encryptedAtRest,
                KeyStoreMode: keyStoreMode,
                ExpiresOn: identity?.ExpiresOn,
                TokenAccount: tokenAccount,
                TokenTenantId: tokenTenant,
                TenantMismatch: tenantMismatch,
                UsernameMismatch: usernameMismatch);
        }
    }
}
