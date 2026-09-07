using System.Text.Json;

namespace Tomix.Provider.Tom;

/// <summary>
/// Resolves the display name for a loaded model. The TOM database name wins; nameless
/// databases (the norm for PBIP folders and hand-written TMDL) fall back to the Fabric
/// <c>.platform</c> displayName, then to the source file or folder name. Server sources
/// have no path, so callers pass the database ID as the final fallback.
/// </summary>
public static class ModelDisplayName
{
    private const string Unnamed = "(unnamed)";

    private static readonly string[] ItemSuffixes = [".SemanticModel", ".Report"];

    public static string Resolve(string? tomName, string? sourcePath = null, string? fallback = null)
    {
        if (!string.IsNullOrWhiteSpace(tomName))
            return tomName;

        var platformName = sourcePath is null ? null : PlatformDisplayName(sourcePath);
        if (!string.IsNullOrWhiteSpace(platformName))
            return platformName;

        var pathName = PathName(sourcePath);
        if (!string.IsNullOrWhiteSpace(pathName))
            return pathName;

        return string.IsNullOrWhiteSpace(fallback) ? Unnamed : fallback;
    }

    /// <summary>Reads <c>metadata.displayName</c> from the sibling <c>.platform</c> file; a missing or malformed one is ignored.</summary>
    private static string? PlatformDisplayName(string sourcePath)
    {
        var directory = Directory.Exists(sourcePath)
            ? sourcePath
            : Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrEmpty(directory))
            return null;

        // PBIP items keep .platform at the item root and the TMDL definition one level down
        // (<Item>.SemanticModel/definition/), so a definition folder also checks its parent.
        var trimmed = directory.TrimEnd('/', '\\');
        string[] platforms =
            string.Equals(Path.GetFileName(trimmed), "definition", StringComparison.OrdinalIgnoreCase)
                ? [Path.Combine(directory, ".platform"), Path.Combine(Path.GetDirectoryName(trimmed)!, ".platform")]
                : [Path.Combine(directory, ".platform")];

        foreach (var platformPath in platforms)
        {
            var displayName = ReadPlatformDisplayName(platformPath);
            if (!string.IsNullOrWhiteSpace(displayName))
                return displayName;
        }

        return null;
    }

    private static string? ReadPlatformDisplayName(string platformPath)
    {
        if (!File.Exists(platformPath))
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(platformPath));
            if (document.RootElement.TryGetProperty("metadata", out var metadata)
                && metadata.TryGetProperty("displayName", out var displayName)
                && displayName.ValueKind == JsonValueKind.String)
                return displayName.GetString();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Display sugar only: a broken .platform must never fail a command.
        }

        return null;
    }

    /// <summary>The file name without extension, or the folder name with the Fabric item suffix stripped.</summary>
    private static string? PathName(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return null;

        var trimmed = sourcePath.TrimEnd('/', '\\');
        if (trimmed.Length == 0)
            return null;

        var name = File.Exists(trimmed)
            ? Path.GetFileNameWithoutExtension(trimmed)
            : Path.GetFileName(trimmed);

        // A PBIP definition folder has no identity of its own; it inherits the item folder's
        // name (<Item>.SemanticModel/definition/).
        if (!File.Exists(trimmed)
            && string.Equals(name, "definition", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(trimmed) is { Length: > 0 } parent)
            name = Path.GetFileName(parent);

        foreach (var suffix in ItemSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return name[..^suffix.Length];
        }

        return name;
    }
}
