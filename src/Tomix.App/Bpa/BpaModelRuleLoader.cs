using System.Text.Json;
using Tomix.Core.Bpa;

namespace Tomix.App.Bpa;

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

    public static async Task<Outcome> LoadAsync(
        IReadOnlyDictionary<string, string>? modelProperties,
        string? baseDirectory,
        bool allowExternal,
        CancellationToken cancellationToken)
    {
        var collections = new List<BpaRuleCollection>();
        var diagnostics = new List<string>();

        LoadEmbedded(modelProperties, collections, diagnostics);
        await LoadExternalAsync(modelProperties, baseDirectory, allowExternal, collections, diagnostics, cancellationToken)
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
                collections.Add(new BpaRuleCollection(BpaRuleSourceKind.ModelEmbedded, "model-embedded", rules));
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
        List<BpaRuleCollection> collections,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        var json = Annotation(properties, ExternalFilesKey);
        if (string.IsNullOrWhiteSpace(json))
            return;

        string[]? entries;
        try
        {
            entries = JsonSerializer.Deserialize<string[]>(json);
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
                        diagnostics.Add($"Skipped remote rule file (pass --allow-external-rules to enable): {entry}");
                        continue;
                    }

                    var remote = await BpaRuleLoader.LoadFromSourceAsync(entry, cancellationToken).ConfigureAwait(false);
                    if (remote.Count > 0)
                        collections.Add(new BpaRuleCollection(BpaRuleSourceKind.External, entry, remote));
                    continue;
                }

                var path = Path.IsPathRooted(entry)
                    ? entry
                    : Path.Combine(baseDirectory ?? Directory.GetCurrentDirectory(), entry);

                if (!File.Exists(path))
                {
                    diagnostics.Add($"External rule file not found: {entry}");
                    continue;
                }

                var rules = BpaRuleLoader.LoadFromFile(path);
                if (rules.Count > 0)
                    collections.Add(new BpaRuleCollection(BpaRuleSourceKind.External, entry, rules));
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or ArgumentException or UriFormatException)
            {
                diagnostics.Add($"Failed to load external rule file '{entry}': {ex.Message}");
            }
        }
    }

    private static string? Annotation(IReadOnlyDictionary<string, string>? properties, string name)
        => properties is not null && properties.TryGetValue($"Annotation:{name}", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static bool IsHttp(string source)
        => Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
