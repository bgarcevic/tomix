using System.Text.Json;

namespace Tomix.App.Connect;

/// <summary>
/// Finds the running Power BI Desktop instance that has a model's files open, so a command that
/// needs an engine (such as <c>refresh</c>) can run against Desktop when it is given the PBIP's
/// files, as Tabular Editor does when Desktop launches it.
/// </summary>
/// <remarks>
/// Desktop opens a <c>.pbip</c>, which names its report folder; the report's
/// <c>definition.pbir</c> names the semantic model folder by path. A model path matches when it is
/// the <c>.pbip</c> itself, that semantic model folder, or anything inside it (its
/// <c>definition</c> folder or <c>definition.pbism</c>). The file Desktop opened is read from its
/// command line, so a project opened from Desktop's own File menu is not found.
/// </remarks>
public static class PowerBiDesktopProjects
{
    /// <summary>
    /// The one running Desktop instance with <paramref name="modelPath"/> open, or null when none
    /// or more than one has it.
    /// </summary>
    public static PowerBiDesktopInstance? FindOpening(string modelPath)
        => FindOpening(modelPath, PowerBiDesktopDiscovery.DiscoverInstances());

    internal static PowerBiDesktopInstance? FindOpening(string modelPath, IEnumerable<PowerBiDesktopInstance> instances)
    {
        var matches = instances
            .Where(instance => instance.OpenedFile is { } opened && Opens(opened, modelPath))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>Whether Desktop having opened <paramref name="openedFile"/> means it has <paramref name="modelPath"/> open.</summary>
    internal static bool Opens(string openedFile, string modelPath)
    {
        if (FullPath(openedFile) is not { } opened || FullPath(modelPath) is not { } target)
            return false;

        return SamePath(opened, target)
            || ModelFolders(opened).Any(folder => SamePath(folder, target) || IsInside(target, folder));
    }

    /// <summary>The semantic model folders behind a file Desktop opened.</summary>
    internal static IEnumerable<string> ModelFolders(string openedFile)
    {
        var directory = Path.GetDirectoryName(openedFile);
        if (directory is null)
            yield break;

        var extension = Path.GetExtension(openedFile);
        if (extension.Equals(".pbism", StringComparison.OrdinalIgnoreCase))
        {
            yield return directory;
            yield break;
        }

        if (!extension.Equals(".pbip", StringComparison.OrdinalIgnoreCase))
            yield break;

        foreach (var report in ReportFolders(openedFile, directory))
        {
            if (DatasetFolder(report) is { } dataset)
                yield return dataset;
        }

        // Desktop's own layout, for a project whose report files cannot be read.
        yield return Path.Combine(directory, Path.GetFileNameWithoutExtension(openedFile) + ".SemanticModel");
    }

    private static IEnumerable<string> ReportFolders(string pbip, string directory)
    {
        var paths = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(pbip));
            if (document.RootElement.TryGetProperty("artifacts", out var artifacts) && artifacts.ValueKind == JsonValueKind.Array)
            {
                foreach (var artifact in artifacts.EnumerateArray())
                {
                    if (artifact.ValueKind == JsonValueKind.Object
                        && artifact.TryGetProperty("report", out var report)
                        && report.ValueKind == JsonValueKind.Object
                        && report.TryGetProperty("path", out var path)
                        && path.GetString() is { Length: > 0 } relative)
                        paths.Add(Path.GetFullPath(Path.Combine(directory, relative)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            // An unreadable project falls back to Desktop's default layout.
        }

        return paths;
    }

    private static string? DatasetFolder(string reportFolder)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(reportFolder, "definition.pbir")));
            return document.RootElement.TryGetProperty("datasetReference", out var reference)
                && reference.ValueKind == JsonValueKind.Object
                && reference.TryGetProperty("byPath", out var byPath)
                && byPath.ValueKind == JsonValueKind.Object
                && byPath.TryGetProperty("path", out var path)
                && path.GetString() is { Length: > 0 } relative
                ? Path.GetFullPath(Path.Combine(reportFolder, relative))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? FullPath(string path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool SamePath(string left, string right)
        => string.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string path, string folder)
        => path.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
