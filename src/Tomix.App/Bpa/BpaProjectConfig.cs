using System.Text.Json;
using System.Text.Json.Serialization;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;
using Tomix.Platform.Configuration;

namespace Tomix.App.Bpa;

/// <summary>
/// The <c>bpa</c> settings a project commits in <c>tomix.json</c> (#236), merged across every
/// layer that set them. Command-line options still win over all of these.
/// </summary>
/// <param name="Enabled">Whether the deploy gate runs; <c>false</c> only when a layer said so.</param>
/// <param name="EnabledSetting">Names the layer that disabled the gate, for the CI log; null while enabled.</param>
/// <param name="FailOn">The gate's severity threshold, or null for the command's default.</param>
/// <param name="FailOnSetting">Names the layer <paramref name="FailOn"/> came from, for errors.</param>
/// <param name="DisabledRuleIds">Rule ids every layer disabled, combined.</param>
/// <param name="Rules">Rule files and URLs, lowest precedence first, anchored at their file.</param>
/// <param name="Files">The files the settings came from, nearest (highest precedence) first.</param>
public sealed record BpaProjectSettings(
    bool Enabled,
    string? EnabledSetting,
    string? FailOn,
    string? FailOnSetting,
    IReadOnlySet<string> DisabledRuleIds,
    IReadOnlyList<BpaRuleSourceEntry> Rules,
    IReadOnlyList<string> Files)
{
    public static readonly BpaProjectSettings None = new(
        true, null, null, null, new HashSet<string>(StringComparer.OrdinalIgnoreCase), [], []);

    /// <summary>An info line naming the files in use, so a CI log shows where its policy came from.</summary>
    public IReadOnlyList<TomixDiagnostic> Attribution => Files.Count == 0
        ? []
        :
        [
            new TomixDiagnostic(
                BpaProjectConfig.LoadedCode,
                DiagnosticSeverity.Info,
                $"BPA settings from {string.Join(", ", Files)}.")
        ];
}

/// <summary>A <c>tomix.json</c> that cannot be read or holds an invalid value.</summary>
public sealed class BpaProjectConfigException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Finds and merges the project's <c>tomix.json</c> files (#236). The more local a setting, the
/// more it wins, lowest to highest: the file <c>TOMIX_PROJECT_CONFIG</c> names, then each
/// <c>tomix.json</c> from the repository root down to the model's directory. Values merge key by
/// key, so a nearer file overrides only what it sets; <c>disabledRuleIds</c> combine across all.
/// </summary>
public static class BpaProjectConfig
{
    public const string FileName = "tomix.json";
    public const string EnvironmentVariable = "TOMIX_PROJECT_CONFIG";
    public const string InvalidCode = "TOMIX_PROJECT_CONFIG_INVALID";
    public const string LoadedCode = "TOMIX_PROJECT_CONFIG_LOADED";
    public const string GateDisabledCode = "TOMIX_BPA_GATE_DISABLED";

    /// <summary>
    /// Where the search starts: the model's folder (a TMDL folder itself, a file's directory), or
    /// the working directory for a remote model, which has no folder of its own.
    /// </summary>
    public static string StartDirectory(ModelReference model)
    {
        if (!model.IsLocalPath)
            return Environment.CurrentDirectory;

        var path = Path.GetFullPath(model.Value);
        return Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
    }

    public static BpaProjectSettings Resolve(ModelReference model, Func<string, string?> environment)
        => Resolve(StartDirectory(model), environment);

    /// <exception cref="BpaProjectConfigException">A file is missing, unreadable, or invalid.</exception>
    public static BpaProjectSettings Resolve(string startDirectory, Func<string, string?> environment)
    {
        var layers = new List<string>();
        var named = environment(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(named))
        {
            var path = Path.GetFullPath(named);
            if (!File.Exists(path))
                throw new BpaProjectConfigException($"Project config file not found: {path} (from {EnvironmentVariable}).");
            layers.Add(path);
        }

        // Discovery returns nearest first; merge farthest first so the nearest is applied last.
        foreach (var path in ProjectFiles.FindUpward(startDirectory, FileName).Reverse())
            if (!layers.Contains(path, StringComparer.OrdinalIgnoreCase))
                layers.Add(path);

        if (layers.Count == 0)
            return BpaProjectSettings.None;

        bool? enabled = null;
        string? enabledSetting = null, failOn = null, failOnSetting = null;
        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rules = new List<BpaRuleSourceEntry>();

        foreach (var path in layers)
        {
            var section = Load(path);
            if (section is null)
                continue;

            if (section.Enabled is { } layerEnabled)
            {
                enabled = layerEnabled;
                enabledSetting = Setting("enabled", path);
            }

            if (section.FailOn is { } layerFailOn)
            {
                if (!BpaFailOn.TryParse(layerFailOn, "failOn", out _, out _))
                    throw new BpaProjectConfigException(
                        $"Invalid 'bpa.failOn' value '{layerFailOn}' in {path}. Expected: error or warning.");
                failOn = layerFailOn;
                failOnSetting = Setting("failOn", path);
            }

            foreach (var id in section.DisabledRuleIds ?? [])
                if (!string.IsNullOrWhiteSpace(id))
                    disabled.Add(id.Trim());

            var directory = Path.GetDirectoryName(path)!;
            foreach (var location in section.Rules ?? [])
                if (!string.IsNullOrWhiteSpace(location))
                    rules.Add(new BpaRuleSourceEntry(
                        BpaRuleSources.AnchorAt(directory, location.Trim()), BpaRuleOrigin.Project, Setting("rules", path)));
        }

        return new BpaProjectSettings(
            enabled ?? true,
            enabled == false ? enabledSetting : null,
            failOn,
            failOnSetting,
            disabled,
            rules,
            [.. Enumerable.Reverse(layers)]);
    }

    private static string Setting(string key, string path) => $"'bpa.{key}' in {path}";

    private static BpaSection? Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return null;
            return JsonSerializer.Deserialize(json, ProjectConfigJsonContext.Default.ProjectConfigDocument)?.Bpa;
        }
        catch (JsonException ex)
        {
            throw new BpaProjectConfigException($"Project config file is invalid: {path}. {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new BpaProjectConfigException($"Project config file cannot be read: {path}. {ex.Message}", ex);
        }
    }
}

/// <summary>The shape of <c>tomix.json</c>. Other top-level sections are left for later features.</summary>
internal sealed class ProjectConfigDocument
{
    public BpaSection? Bpa { get; set; }
}

/// <summary>
/// The <c>bpa</c> section. An unknown key here fails, so a misspelled <c>failOn</c> cannot leave
/// the gate silently on its default.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class BpaSection
{
    public bool? Enabled { get; set; }
    public string? FailOn { get; set; }
    public List<string>? DisabledRuleIds { get; set; }
    public List<string>? Rules { get; set; }
}

/// <summary>
/// Reads the user-authored <c>tomix.json</c>: keys match in any case, and comments and trailing
/// commas are allowed.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ProjectConfigDocument))]
internal sealed partial class ProjectConfigJsonContext : JsonSerializerContext;
