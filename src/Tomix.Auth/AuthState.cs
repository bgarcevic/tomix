using System.Text.Json;
using System.Text.Json.Serialization;
using Tomix.Core.Authentication;

namespace Tomix.Auth;

/// <summary>
/// Sidecar metadata persisted next to the MSAL cache. MSAL's cache records accounts and tokens
/// but not which flow produced them, so we keep the method (and, for service principals where
/// no user account exists in the cache, the identity/expiry) here for <c>auth status</c>.
/// </summary>
public sealed record AuthState(
    AuthMethod Method,
    string Username,
    string? TenantId,
    string? ClientId,
    string? Endpoint,
    DateTimeOffset? ExpiresOn);

internal sealed class AuthStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;

    public AuthStateStore(string path) => _path = path;

    public AuthState? Load()
    {
        if (!File.Exists(_path))
            return null;

        var json = File.ReadAllText(_path);
        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<AuthState>(json, SerializerOptions);
    }

    public void Save(AuthState state)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(_path, JsonSerializer.Serialize(state, SerializerOptions));
    }

    public bool Delete()
    {
        if (!File.Exists(_path))
            return false;

        File.Delete(_path);
        return true;
    }
}
