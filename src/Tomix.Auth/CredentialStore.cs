using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tomix.Core.Authentication;
using Tomix.Core.Configuration;

namespace Tomix.Auth;

internal sealed class CredentialStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;

    public CredentialStore(string? path = null)
        => _path = path ?? Path.Combine(TomixPaths.AuthDirectory, "tomix-credentials.enc");

    public void Save(AuthLoginOptions options)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var payload = new CredentialPayload(
            options.ClientId,
            options.Tenant,
            options.ClientSecret,
            options.CertificatePath,
            options.CertificatePassword);

        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        var plaintext = Encoding.UTF8.GetBytes(json);
        var encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        AtomicFile.WriteAllBytes(_path, encrypted);
    }

    public AuthLoginOptions? Load(AuthMethod method, string? endpoint)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        if (!File.Exists(_path))
            return null;

        try
        {
            var encrypted = File.ReadAllBytes(_path);
            var plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(plaintext);
            var payload = JsonSerializer.Deserialize<CredentialPayload>(json, SerializerOptions);

            if (payload is null || string.IsNullOrWhiteSpace(payload.ClientId) || string.IsNullOrWhiteSpace(payload.Tenant))
                return null;

            if (!string.IsNullOrWhiteSpace(payload.CertificatePath))
            {
                return new AuthLoginOptions(
                    method,
                    endpoint,
                    payload.Tenant,
                    payload.ClientId,
                    CertificatePath: payload.CertificatePath,
                    CertificatePassword: payload.CertificatePassword);
            }

            if (!string.IsNullOrWhiteSpace(payload.ClientSecret))
            {
                return new AuthLoginOptions(
                    method,
                    endpoint,
                    payload.Tenant,
                    payload.ClientId,
                    ClientSecret: payload.ClientSecret);
            }

            return null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public bool Delete()
    {
        if (!File.Exists(_path))
            return false;

        File.Delete(_path);
        return true;
    }

    private sealed record CredentialPayload(
        string? ClientId,
        string? Tenant,
        string? ClientSecret,
        string? CertificatePath,
        string? CertificatePassword);
}
