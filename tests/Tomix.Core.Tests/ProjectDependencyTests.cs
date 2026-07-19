using System.Xml.Linq;

namespace Tomix.Core.Tests;

public sealed class ProjectDependencyTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedReferences =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["Tomix.Core"] = Set(),
            ["Tomix.Platform"] = Set(),
            ["Tomix.App"] = Set("Tomix.Core", "Tomix.Platform"),
            ["Tomix.Auth"] = Set("Tomix.Core", "Tomix.Platform"),
            ["Tomix.Provider.Tom"] = Set("Tomix.Core"),
            ["Tomix.Provider.Tmdl"] = Set("Tomix.Core", "Tomix.Provider.Tom"),
            ["Tomix.Provider.Vpax"] = Set("Tomix.Core"),
            ["Tomix.Cli"] = Set(
                "Tomix.App",
                "Tomix.Auth",
                "Tomix.Core",
                "Tomix.Provider.Tom",
                "Tomix.Provider.Tmdl",
                "Tomix.Provider.Vpax")
        };

    [Fact]
    public void ProductionProjects_FollowDocumentedDependencyGraph()
    {
        var root = FindRepositoryRoot();
        var projectFiles = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "Tomix.*.csproj", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetFileNameWithoutExtension(path),
                path => path,
                StringComparer.Ordinal);

        Assert.True(
            AllowedReferences.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(projectFiles.Keys),
            $"Discovered projects [{string.Join(", ", projectFiles.Keys.Order())}], "
            + $"but the dependency policy covers [{string.Join(", ", AllowedReferences.Keys.Order())}].");

        foreach (var (project, allowed) in AllowedReferences)
        {
            var projectFile = projectFiles[project];
            var actual = XDocument.Load(projectFile)
                .Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(include => !string.IsNullOrWhiteSpace(include))
                .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', '/')))
                .ToHashSet(StringComparer.Ordinal);

            Assert.True(
                allowed.SetEquals(actual),
                $"{project} references [{string.Join(", ", actual.Order())}], expected [{string.Join(", ", allowed.Order())}].");
        }
    }

    private static HashSet<string> Set(params string[] projects)
        => new(projects, StringComparer.Ordinal);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tomix.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
    }
}
