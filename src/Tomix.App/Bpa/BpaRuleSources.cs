using Tomix.App.Config;
using Tomix.Core.Bpa;
using Tomix.Core.Configuration;
using Tomix.Platform.Configuration;

namespace Tomix.App.Bpa;

/// <summary>One rule file or URL named by a link of the rule-source chain.</summary>
public sealed record BpaRuleSourceEntry(string Location, BpaRuleOrigin Origin, string Setting);

/// <summary>
/// The configured rule-source chain (#233), lowest to highest precedence: the config-dir
/// <c>bpa-rules.json</c>, the <c>bpa.rules</c> config key, the <c>TOMIX_BPA_RULES</c> environment
/// variable, then <c>--rules</c>. All links load as user rules, so for the same rule id a later
/// link overrides an earlier one; the model's own annotations still override them all.
/// </summary>
public static class BpaRuleSources
{
    public const string EnvironmentVariable = "TOMIX_BPA_RULES";

    /// <summary>Separates entries in <c>bpa.rules</c> and <c>TOMIX_BPA_RULES</c> on every platform.</summary>
    public const char Separator = ';';

    /// <summary>
    /// The chain's file and URL entries in load order. The config-dir <c>bpa-rules.json</c> is
    /// not listed: it is optional, so the caller loads it only when it exists.
    /// </summary>
    /// <param name="configDirectory">Where <c>bpa.rules</c> is read from; null skips that link.</param>
    /// <param name="optionName">The command's option for <paramref name="optionFiles"/>, named in errors.</param>
    public static IReadOnlyList<BpaRuleSourceEntry> Resolve(
        string? configDirectory,
        Func<string, string?> environment,
        IReadOnlyList<string>? optionFiles,
        string optionName = "--rules")
    {
        var entries = new List<BpaRuleSourceEntry>();

        if (configDirectory is not null)
        {
            new TomixConfigStore(TomixPaths.ConfigFileIn(configDirectory)).Load()
                .TryGetValue(ConfigKeys.BpaRules, out var configured);
            foreach (var location in Split(configured))
                entries.Add(new BpaRuleSourceEntry(
                    AnchorAt(configDirectory, location), BpaRuleOrigin.Config, $"config '{ConfigKeys.BpaRules}'"));
        }

        foreach (var location in Split(environment(EnvironmentVariable)))
            entries.Add(new BpaRuleSourceEntry(location, BpaRuleOrigin.Environment, EnvironmentVariable));

        foreach (var location in optionFiles ?? [])
            if (!string.IsNullOrWhiteSpace(location))
                entries.Add(new BpaRuleSourceEntry(location, BpaRuleOrigin.Option, optionName));

        return entries;
    }

    /// <summary>
    /// Loads one chain entry. A failure names the setting that pointed at the source, so a CI log
    /// says whether to fix the pipeline variable, the config file, or the command line.
    /// </summary>
    public static async Task<IReadOnlyList<BpaRule>> LoadEntryAsync(
        BpaRuleSourceEntry entry, HttpClient? httpClient, CancellationToken cancellationToken)
    {
        try
        {
            return await BpaRuleLoader.LoadFromSourceAsync(entry.Location, httpClient, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException ex) when (entry.Origin != BpaRuleOrigin.Option)
        {
            throw new FileNotFoundException($"{ex.Message} (from {entry.Setting})", ex.FileName, ex);
        }
        catch (HttpRequestException ex) when (entry.Origin != BpaRuleOrigin.Option)
        {
            throw new HttpRequestException($"{ex.Message} (from {entry.Setting}: {entry.Location})", ex, ex.StatusCode);
        }
    }

    /// <summary>
    /// The rules a deploy gate checks: the <c>standard</c> ruleset, then <c>bpa.rules</c>,
    /// <c>TOMIX_BPA_RULES</c>, and the command's own rule files, resolved like <c>bpa run</c> so
    /// a later source overrides an earlier one for the same id. The personal config-dir
    /// <c>bpa-rules.json</c> and the model's annotations stay <c>bpa run</c>-only.
    /// </summary>
    public static async Task<BpaResolvedRules> LoadGateRulesAsync(
        string? configDirectory,
        Func<string, string?> environment,
        IReadOnlyList<string>? optionFiles,
        string optionName,
        HttpClient? httpClient,
        CancellationToken cancellationToken)
    {
        var collections = new List<BpaRuleCollection>
        {
            new(BpaRuleSourceKind.Machine, BpaRuleLoader.StandardRuleset,
                await BpaRuleLoader.LoadRulesetAsync(null, httpClient, cancellationToken).ConfigureAwait(false),
                BpaRuleOrigin.Ruleset)
        };

        foreach (var entry in Resolve(configDirectory, environment, optionFiles, optionName))
            collections.Add(new BpaRuleCollection(
                BpaRuleSourceKind.User, entry.Location,
                await LoadEntryAsync(entry, httpClient, cancellationToken).ConfigureAwait(false),
                entry.Origin));

        return BpaRuleResolver.ResolveWithSources(collections);
    }

    private static IEnumerable<string> Split(string? value)
        => (value ?? "").Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// A relative <c>bpa.rules</c> path is anchored at the config directory, so the setting means
    /// the same file from any working directory. URLs and absolute paths pass through.
    /// </summary>
    private static string AnchorAt(string configDirectory, string location)
        => Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            || Path.IsPathRooted(location)
            ? location
            : Path.GetFullPath(Path.Combine(configDirectory, location));
}
