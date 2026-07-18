using System.Text.Json;
using Tomix.Core.Authentication;
using Tomix.Core.Models;

namespace Tomix.Provider.Tmdl;

public sealed class TmdlModelProvider : IModelProvider
{
    private readonly IAccessTokenProvider? _tokenProvider;

    public TmdlModelProvider(IAccessTokenProvider? tokenProvider = null) => _tokenProvider = tokenProvider;

    public bool CanOpen(ModelReference reference) => TryResolveFolder(reference.Value, out _);

    public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryResolveFolder(reference.Value, out var folder))
            throw new DirectoryNotFoundException($"No TMDL model folder found for '{reference.Value}'.");

        return Task.FromResult<IModelSession>(new TmdlModelSession(folder, _tokenProvider));
    }

    private static bool TryResolveFolder(string value, out string folder)
    {
        folder = "";

        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (Directory.Exists(value))
            return TryResolveFolderFromDirectory(Path.GetFullPath(value), out folder);

        if (!File.Exists(value))
            return false;

        var extension = Path.GetExtension(value);
        if (extension.Equals(".pbip", StringComparison.OrdinalIgnoreCase))
            return TryResolveFolderFromPbip(Path.GetFullPath(value), out folder);

        if (extension.Equals(".pbism", StringComparison.OrdinalIgnoreCase))
            return TryResolveFolderFromDirectory(Path.GetDirectoryName(Path.GetFullPath(value))!, out folder);

        return false;
    }

    private static bool TryResolveFolderFromDirectory(string directory, out string folder)
    {
        if (IsTmdlFolder(directory))
        {
            folder = directory;
            return true;
        }

        var definition = Path.Combine(directory, "definition");
        if (Directory.Exists(definition) && IsTmdlFolder(definition))
        {
            folder = definition;
            return true;
        }

        folder = "";
        return false;
    }

    private static bool TryResolveFolderFromPbip(string pbipPath, out string folder)
    {
        var directory = Path.GetDirectoryName(pbipPath)!;
        foreach (var candidate in CandidateSemanticModelFolders(pbipPath))
        {
            var fullPath = Path.GetFullPath(Path.Combine(directory, candidate));
            if (TryResolveFolderFromDirectory(fullPath, out folder))
                return true;
        }

        folder = "";
        return false;
    }

    private static IEnumerable<string> CandidateSemanticModelFolders(string pbipPath)
    {
        var stem = Path.GetFileNameWithoutExtension(pbipPath);
        yield return $"{stem}.SemanticModel";

        foreach (var path in ReadSemanticModelArtifacts(pbipPath))
            yield return path;

        var directory = Path.GetDirectoryName(pbipPath)!;
        foreach (var semanticModel in Directory.EnumerateDirectories(directory, "*.SemanticModel"))
            yield return Path.GetFileName(semanticModel);
    }

    private static IEnumerable<string> ReadSemanticModelArtifacts(string pbipPath)
    {
        using var document = TryParseJson(pbipPath);
        if (document is null)
            yield break;

        var root = document.RootElement;
        if (root.TryGetProperty("artifacts", out var artifacts) &&
            artifacts.ValueKind == JsonValueKind.Array)
        {
            foreach (var artifact in artifacts.EnumerateArray())
            {
                if (!artifact.TryGetProperty("semanticModel", out var semanticModel) ||
                    !semanticModel.TryGetProperty("path", out var path) ||
                    path.ValueKind != JsonValueKind.String)
                    continue;

                var value = path.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    yield return value;
            }
        }
    }

    private static JsonDocument? TryParseJson(string path)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsTmdlFolder(string directory)
        => File.Exists(Path.Combine(directory, "database.tmdl")) &&
           File.Exists(Path.Combine(directory, "model.tmdl"));
}
