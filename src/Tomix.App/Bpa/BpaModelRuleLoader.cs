using System.Text.Json;
using Tomix.Core.Bpa;
using Tomix.Core.Models;

namespace Tomix.App.Bpa;

/// <summary>
/// Which command surfaces the loader's diagnostics. Remedy hints reference command options,
/// so they must only suggest options the invoked command actually accepts.
/// </summary>
public enum BpaRuleHintContext
{
    Run,
    List
}

/// <summary>
/// Loads BPA rule collections referenced by the model itself: rules embedded in the
/// <c>BestPracticeAnalyzer</c> annotation, and external collections listed in the
/// <c>BestPracticeAnalyzer_ExternalRuleFiles</c> annotation. Loading is best-effort — a malformed
/// annotation or an unreachable file yields a diagnostic and never throws, so analysis continues.
/// </summary>
public static class BpaModelRuleLoader
{
    /// <summary>Annotation holding embedded rule definitions (JSON array, bpa-rules.json shape).</summary>
    public const string EmbeddedKey = "BestPracticeAnalyzer";

    /// <summary>Historical misspelled embedded-rules key.</summary>
    public const string EmbeddedLegacyKey = "BestPractizeAnalyzer";

    /// <summary>Annotation holding a JSON array of external rule file paths/URLs.</summary>
    public const string ExternalFilesKey = "BestPracticeAnalyzer_ExternalRuleFiles";

    public sealed record Outcome(
        IReadOnlyList<BpaRuleCollection> Collections,
        IReadOnlyList<string> Diagnostics);

    /// <summary>
    /// The directory relative external-file entries resolve against. The opened session's source
    /// path wins when there is one: a .pbip/.pbism/project-root entry point opens the nested TMDL
    /// definition folder, which is where community tooling anchors these paths. Falls back to the
    /// model reference for a session with no local source path (a connected model), and yields null
    /// when neither is a local path — the caller then treats entries as relative to the cwd.
    /// Pass <paramref name="session"/> as null to resolve from the reference alone, which is what a
    /// staged run needs: it opens a working copy, but the annotation's paths point at the original.
    /// </summary>
    public static string? ResolveBaseDirectory(IModelSession? session, ModelReference model)
        => FolderOf(session?.SourcePath) ?? (model.IsLocalPath ? FolderOf(model.Value) : null);

    /// <summary>The path itself when it is a folder, its containing folder when it is a file.</summary>
    private static string? FolderOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return Directory.Exists(path)
                ? path
                : Path.GetDirectoryName(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    public static async Task<Outcome> LoadAsync(
        IReadOnlyDictionary<string, string>? modelProperties,
        string? baseDirectory,
        bool allowExternal,
        CancellationToken cancellationToken)
        => await LoadAsync(
            modelProperties, baseDirectory, allowExternal, BpaRuleHintContext.Run, httpClient: null, cancellationToken);

    public static async Task<Outcome> LoadAsync(
        IReadOnlyDictionary<string, string>? modelProperties,
        string? baseDirectory,
        bool allowExternal,
        BpaRuleHintContext hints,
        HttpClient? httpClient,
        CancellationToken cancellationToken,
        ModelReference? model = null)
    {
        var collections = new List<BpaRuleCollection>();
        var diagnostics = new List<string>();

        LoadEmbedded(modelProperties, collections, diagnostics);
        await LoadExternalAsync(
                modelProperties, baseDirectory, allowExternal, hints, model, collections, diagnostics, httpClient, cancellationToken)
            .ConfigureAwait(false);

        return new Outcome(collections, diagnostics);
    }

    private static void LoadEmbedded(
        IReadOnlyDictionary<string, string>? properties,
        List<BpaRuleCollection> collections,
        List<string> diagnostics)
    {
        var json = Annotation(properties, EmbeddedKey) ?? Annotation(properties, EmbeddedLegacyKey);
        if (string.IsNullOrWhiteSpace(json))
            return;

        try
        {
            var rules = BpaRuleLoader.LoadFromJson(json);
            if (rules.Count > 0)
                collections.Add(new BpaRuleCollection(BpaRuleSourceKind.ModelEmbedded, "model-embedded", rules, BpaRuleOrigin.Model));
        }
        catch (JsonException ex)
        {
            diagnostics.Add($"Model-embedded BPA rules ({EmbeddedKey}) could not be parsed: {ex.Message}");
        }
    }

    private static async Task LoadExternalAsync(
        IReadOnlyDictionary<string, string>? properties,
        string? baseDirectory,
        bool allowExternal,
        BpaRuleHintContext hints,
        ModelReference? model,
        List<BpaRuleCollection> collections,
        List<string> diagnostics,
        HttpClient? httpClient,
        CancellationToken cancellationToken)
    {
        var json = Annotation(properties, ExternalFilesKey);
        if (string.IsNullOrWhiteSpace(json))
            return;

        string[]? entries;
        try
        {
            entries = JsonSerializer.Deserialize(json, AppJsonContext.Default.StringArray);
        }
        catch (JsonException ex)
        {
            diagnostics.Add($"{ExternalFilesKey} annotation is not valid JSON: {ex.Message}");
            return;
        }

        if (entries is null)
            return;

        // Entries are added in declared order; BpaRuleResolver gives earlier External entries
        // precedence over later ones (spec §7).
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            try
            {
                if (IsHttp(entry))
                {
                    if (!allowExternal)
                    {
                        diagnostics.Add(hints == BpaRuleHintContext.Run
                            ? $"Skipped remote rule file (pass --allow-external-rules to enable): {entry}"
                            : $"Skipped remote rule file (never fetched when listing; 'bpa run --allow-external-rules' loads it): {entry}");
                        continue;
                    }

                    var remote = await BpaRuleLoader
                        .LoadFromSourceAsync(entry, httpClient, cancellationToken)
                        .ConfigureAwait(false);
                    if (remote.Count > 0)
                        collections.Add(new BpaRuleCollection(BpaRuleSourceKind.External, entry, remote, BpaRuleOrigin.Model));
                    continue;
                }

                // Community tooling writes these paths with Windows separators; normalize so a
                // "..\..\rules.json" entry resolves on every platform.
                var normalized = entry.Replace('\\', Path.DirectorySeparatorChar);
                var candidates = Candidates(normalized, baseDirectory);
                var path = candidates.FirstOrDefault(File.Exists);

                if (path is null)
                {
                    diagnostics.Add(
                        $"External rule file not found: {entry} (resolved: {string.Join(" or ", candidates)}). "
                        + (hints == BpaRuleHintContext.Run ? "Skip with --no-model-rules, or remove" : "Remove")
                        + $" the reference: tx set {SetTarget(model)}--set annotation:BestPracticeAnalyzer_ExternalRuleFiles= --save");
                    continue;
                }

                var rules = BpaRuleLoader.LoadFromFile(path);
                if (rules.Count > 0)
                    collections.Add(new BpaRuleCollection(BpaRuleSourceKind.External, entry, rules, BpaRuleOrigin.Model));
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or ArgumentException or UriFormatException)
            {
                diagnostics.Add($"Failed to load external rule file '{entry}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Where a relative entry may live, most specific first. Tools disagree on the anchor for a
    /// folder-based model: some resolve against the model folder itself (the TMDL definition
    /// folder), while Tabular Editor resolves against the folder containing it, so a PBIP
    /// annotation written there points one level higher (from X.SemanticModel). Both are tried.
    /// </summary>
    private static IReadOnlyList<string> Candidates(string normalized, string? baseDirectory)
    {
        if (Path.IsPathRooted(normalized))
            return [Path.GetFullPath(normalized)];

        var anchor = Path.GetFullPath(baseDirectory ?? Directory.GetCurrentDirectory());
        var own = Path.GetFullPath(Path.Combine(anchor, normalized));
        return Path.GetDirectoryName(anchor) is { } parent
            ? [own, Path.GetFullPath(Path.Combine(parent, normalized))]
            : [own];
    }

    /// <summary>
    /// The model argument for the remedy's 'tx set', followed by a space: the local path as the user
    /// passed it (quoted when needed), or nothing for a connected model, which set reaches through
    /// the active connection. A trailing separator is dropped so a quoted Windows path does not end
    /// in \" and escape its own quote.
    /// </summary>
    private static string SetTarget(ModelReference? model)
    {
        if (model is null)
            return ". ";
        if (!model.IsLocalPath)
            return "";

        var path = model.Value.TrimEnd('\\', '/');
        if (path.Length == 0)
            path = model.Value;
        return path.Any(c => char.IsWhiteSpace(c) || c is '\'' or '"' or '&' or ';' or '(' or ')')
            ? $"\"{path}\" "
            : path + " ";
    }

    private static string? Annotation(IReadOnlyDictionary<string, string>? properties, string name)
        => properties is not null && properties.TryGetValue($"Annotation:{name}", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static bool IsHttp(string source)
        => Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
