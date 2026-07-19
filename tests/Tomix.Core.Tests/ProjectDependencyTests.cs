using System.Xml.Linq;

namespace Tomix.Core.Tests;

public sealed class ProjectDependencyTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedReferences =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["Tomix.Core"] = Set(),
            ["Tomix.App"] = Set("Tomix.Core"),
            ["Tomix.Auth"] = Set("Tomix.Core"),
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

        foreach (var (project, allowed) in AllowedReferences)
        {
            var projectFile = Path.Combine(root, "src", project, $"{project}.csproj");
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
