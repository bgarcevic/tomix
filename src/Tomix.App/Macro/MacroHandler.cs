using System.Text.Json;
using System.Text.Json.Serialization;
using Tomix.App.Config;
using Tomix.Core.Configuration;
using Tomix.Core.Results;

namespace Tomix.App.Macro;

public sealed class MacroHandler
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public TomixResult<MacroFileResult> Init(string? explicitPath, bool force)
    {
        var path = ResolvePath(explicitPath);
        if (File.Exists(path) && !force)
            return TomixResult<MacroFileResult>.Fail(
                "TOMIX_MACRO_FILE_EXISTS",
                $"Macros file already exists: {path}. Use --force to overwrite.",
                exitCode: 1);

        Save(path, new MacroDocument([]));
        return TomixResult<MacroFileResult>.Ok(new MacroFileResult(path));
    }

    public TomixResult<MacroListResult> List(string? explicitPath)
    {
        var path = ResolvePath(explicitPath);
        if (!File.Exists(path))
            return TomixResult<MacroListResult>.Ok(new MacroListResult(path, []));

        var document = Load(path);
        return TomixResult<MacroListResult>.Ok(new MacroListResult(path, Project(document.Actions)));
    }

    public TomixResult<MacroSavedResult> Add(MacroAddRequest request)
    {
        var path = ResolvePath(request.MacrosPath);
        var document = File.Exists(path)
            ? Load(path)
            : new MacroDocument([]);

        var execute = request.Execute;
        if (!string.IsNullOrWhiteSpace(request.ScriptPath))
        {
            if (!File.Exists(request.ScriptPath))
                return TomixResult<MacroSavedResult>.Fail(
                    "TOMIX_MACRO_SCRIPT_NOT_FOUND",
                    $"Macro script file not found: {request.ScriptPath}",
                    exitCode: 1);

            execute = File.ReadAllText(request.ScriptPath);
        }

        var nextId = document.Actions.Count == 0
            ? 0
            : document.Actions.Max(action => action.Id) + 1;
        var action = new MacroAction(
            Id: nextId,
            Name: request.Name,
            Enabled: EmptyToNull(request.Enabled),
            Execute: EmptyToNull(execute),
            Tooltip: EmptyToNull(request.Tooltip),
            ValidContexts: NormalizeContexts(request.ValidContexts) ?? "None");

        document.Actions.Add(action);
        Save(path, document);
        return TomixResult<MacroSavedResult>.Ok(new MacroSavedResult(path, Project(action), "added"));
    }

    public TomixResult<MacroSavedResult> Set(string? explicitPath, string nameOrId, string property, string value)
    {
        var path = ResolvePath(explicitPath);
        if (!File.Exists(path))
            return MissingFile<MacroSavedResult>(path);

        var document = Load(path);
        var action = Find(document, nameOrId);
        if (action is null)
            return MissingMacro<MacroSavedResult>(nameOrId);

        var updated = property switch
        {
            "name" => action with { Name = value },
            "execute" => action with { Execute = EmptyToNull(value) },
            "enabled" => action with { Enabled = EmptyToNull(value) },
            "tooltip" => action with { Tooltip = EmptyToNull(value) },
            "validContexts" => action with { ValidContexts = NormalizeContexts(value) },
            _ => null
        };

        if (updated is null)
            return TomixResult<MacroSavedResult>.Fail(
                "TOMIX_MACRO_UNKNOWN_PROPERTY",
                "Property must be one of: name, execute, enabled, tooltip, validContexts.",
                exitCode: 2);

        var index = document.Actions.IndexOf(action);
        document.Actions[index] = updated;
        Save(path, document);
        return TomixResult<MacroSavedResult>.Ok(new MacroSavedResult(path, Project(updated), "updated"));
    }

    public TomixResult<MacroSavedResult> Remove(string? explicitPath, string nameOrId)
    {
        var path = ResolvePath(explicitPath);
        if (!File.Exists(path))
            return MissingFile<MacroSavedResult>(path);

        var document = Load(path);
        var action = Find(document, nameOrId);
        if (action is null)
            return MissingMacro<MacroSavedResult>(nameOrId);

        document.Actions.Remove(action);
        Save(path, document);
        return TomixResult<MacroSavedResult>.Ok(new MacroSavedResult(path, Project(action), "removed"));
    }

    public TomixResult<MacroSortResult> Sort(string? explicitPath)
    {
        var path = ResolvePath(explicitPath);
        if (!File.Exists(path))
            return MissingFile<MacroSortResult>(path);

        var document = Load(path);
        var sorted = document.Actions
            .OrderBy(action => SplitName(action.Name).Folder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(action => SplitName(action.Name).DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select((action, index) => action with { Id = index })
            .ToList();

        document.Actions.Clear();
        document.Actions.AddRange(sorted);
        Save(path, document);
        return TomixResult<MacroSortResult>.Ok(new MacroSortResult(path, sorted.Count));
    }

    private static TomixResult<T> MissingFile<T>(string path)
        => TomixResult<T>.Fail(
            "TOMIX_MACRO_FILE_NOT_FOUND",
            $"Macros file not found: {path}. Run 'tx macro init' to create one, or pass --macros <path>.",
            exitCode: 1);

    private static TomixResult<T> MissingMacro<T>(string nameOrId)
        => TomixResult<T>.Fail(
            "TOMIX_MACRO_NOT_FOUND",
            $"Macro not found: {nameOrId}",
            exitCode: 1);

    private static string ResolvePath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return Path.GetFullPath(explicitPath);

        var envPath = Environment.GetEnvironmentVariable("TOMIX_MACROS_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
            return Path.GetFullPath(envPath);

        var compatibilityEnvPath = Environment.GetEnvironmentVariable("TE_MACROS_PATH");
        if (!string.IsNullOrWhiteSpace(compatibilityEnvPath))
            return Path.GetFullPath(compatibilityEnvPath);

        var config = new TomixConfigStore().Load();
        if (config.TryGetValue(ConfigKeys.Macros, out var configPath) && !string.IsNullOrWhiteSpace(configPath))
            return Path.GetFullPath(configPath);

        return Path.Combine(TomixPaths.ConfigDirectory, "macros.json");
    }

    private static MacroDocument Load(string path)
    {
        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
            return new MacroDocument([]);

        return JsonSerializer.Deserialize<MacroDocument>(json, SerializerOptions) ?? new MacroDocument([]);
    }

    private static void Save(string path, MacroDocument document)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonSerializer.Serialize(document, SerializerOptions) + Environment.NewLine);
    }

    private static IReadOnlyList<MacroProjection> Project(IReadOnlyList<MacroAction> actions)
        => actions.Select(Project).ToList();

    private static MacroProjection Project(MacroAction action)
    {
        var (folder, displayName) = SplitName(action.Name);
        return new MacroProjection(
            action.Id,
            action.Name,
            displayName,
            folder,
            action.Enabled,
            action.Tooltip,
            LowercaseContexts(action.ValidContexts));
    }

    private static MacroAction? Find(MacroDocument document, string nameOrId)
    {
        if (int.TryParse(nameOrId, out var id))
            return document.Actions.FirstOrDefault(action => action.Id == id);

        return document.Actions.FirstOrDefault(action => action.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase));
    }

    private static (string? Folder, string DisplayName) SplitName(string name)
    {
        var index = name.LastIndexOf('\\');
        return index < 0
            ? (null, name)
            : (name[..index], name[(index + 1)..]);
    }

    private static string? NormalizeContexts(string? contexts)
    {
        if (string.IsNullOrWhiteSpace(contexts))
            return null;

        var values = contexts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(TitleCaseContext)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ContextOrder)
            .ThenBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return values.Length == 0 ? null : string.Join(", ", values);
    }

    private static string? LowercaseContexts(string? contexts)
        => string.IsNullOrWhiteSpace(contexts)
            ? null
            : string.Join(", ", contexts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => value.ToLowerInvariant()));

    private static string TitleCaseContext(string value)
    {
        if (value.Length == 0)
            return value;

        return char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
    }

    private static int ContextOrder(string value)
        => value.ToLowerInvariant() switch
        {
            "table" => 0,
            "column" => 1,
            "measure" => 2,
            _ => 100
        };

    private static string? EmptyToNull(string? value)
        => string.IsNullOrEmpty(value) ? null : value;
}

public sealed record MacroAddRequest(
    string? MacrosPath,
    string Name,
    string? Execute,
    string? ScriptPath,
    string? Tooltip,
    string? ValidContexts,
    string? Enabled);

public sealed record MacroFileResult(string Path);

public sealed record MacroListResult(string Path, IReadOnlyList<MacroProjection> Macros);

public sealed record MacroSavedResult(string Path, MacroProjection Macro, string Action);

public sealed record MacroSortResult(string Path, int Count);

public sealed record MacroProjection(
    int Id,
    string Name,
    string DisplayName,
    string? Folder,
    string? Enabled,
    string? Tooltip,
    string? ValidContexts);

internal sealed record MacroDocument([property: JsonPropertyName("actions")] List<MacroAction> Actions);

internal sealed record MacroAction(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] string? Enabled = null,
    [property: JsonPropertyName("execute")] string? Execute = null,
    [property: JsonPropertyName("tooltip")] string? Tooltip = null,
    [property: JsonPropertyName("validContexts")] string? ValidContexts = null);
