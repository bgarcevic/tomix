using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tomix.Core.Authentication;
using Tomix.Platform.Configuration;

namespace Tomix.Auth;

/// <summary>
/// Persists service-principal credentials for silent token renewal across processes.
/// Windows encrypts the payload with DPAPI (current user). macOS/Linux store it as an
/// owner-only (0600) file — the OS keychain is deliberately avoided because Keychain
/// authorization prompts block non-interactive runs and CI containers lack libsecret;
/// <c>--save false</c> opts out entirely. Load refuses a Unix file whose permissions
/// allow group/other access, mirroring ssh's strictness.
/// </summary>
internal sealed class CredentialStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerOnlyDirectory = OwnerOnlyFile | UnixFileMode.UserExecute;

    private readonly string _path;

    public CredentialStore(string? path = null)
        => _path = path ?? Path.Combine(TomixPaths.AuthDirectory, "tomix-credentials.enc");

    /// <summary>Human-readable storage description for the post-login notice.</summary>
    public string StorageDescription
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "DPAPI-encrypted file (current user)"
            : $"owner-only file at {_path}";

    public void Save(AuthLoginOptions options)
    {
        var payload = new CredentialPayload(
            options.ClientId,
            options.Tenant,
            options.ClientSecret,
            options.CertificatePath,
            options.CertificatePassword);

        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        var plaintext = Encoding.UTF8.GetBytes(json);

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                File.SetUnixFileMode(directory, OwnerOnlyDirectory);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            AtomicFile.WriteAllBytes(_path, encrypted);
            return;
        }

        // Atomic owner-only write. AtomicFile can't be used here because the temp file must
        // never be group/other readable, even briefly; the rename carries the 0600 mode over.
        var temp = $"{_path}.tmp-{Guid.NewGuid():N}";
        try
        {
            using (var stream = new FileStream(temp, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = OwnerOnlyFile
            }))
            {
                stream.Write(plaintext);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best-effort cleanup */ }
            throw;
        }
    }

    public AuthLoginOptions? Load(AuthMethod method, string? endpoint)
    {
        if (!File.Exists(_path))
            return null;

        try
        {
            string json;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var encrypted = File.ReadAllBytes(_path);
                var plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                json = Encoding.UTF8.GetString(plaintext);
            }
            else
            {
                const UnixFileMode groupOrOther =
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                if ((File.GetUnixFileMode(_path) & groupOrOther) != 0)
                    return null;

                json = File.ReadAllText(_path);
            }

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
        catch (Exception ex) when (ex is CryptographicException or JsonException)
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
