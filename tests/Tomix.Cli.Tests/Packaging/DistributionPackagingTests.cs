using System.Xml.Linq;

namespace Tomix.Cli.Tests.Packaging;

public sealed class DistributionPackagingTests
{
    private static string RepositoryRoot => FindRepositoryRoot();

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

    private static readonly string[] RequiredRuntimeIdentifiers =
    [
        "win-x64",
        "win-arm64",
        "osx-x64",
        "osx-arm64",
        "linux-x64",
        "linux-arm64"
    ];

    [Fact]
    public void CliProjectDeclaresRequiredRuntimeIdentifiers()
    {
        var projectPath = Path.Combine(RepositoryRoot, "src", "Tomix.Cli", "Tomix.Cli.csproj");
        var document = XDocument.Load(projectPath);

        var declared = document.Descendants("RuntimeIdentifiers")
            .Single()
            .Value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(RequiredRuntimeIdentifiers.Order(StringComparer.Ordinal), declared);
    }

    [Fact]
    public void ReleaseWorkflowPublishesRequiredRuntimeArchives()
    {
        var workflowPath = Path.Combine(RepositoryRoot, ".github", "workflows", "release.yml");
        var workflow = File.ReadAllText(workflowPath);

        foreach (var runtimeIdentifier in RequiredRuntimeIdentifiers)
            Assert.Contains($"rid: {runtimeIdentifier}", workflow);

        Assert.Contains("dotnet publish src/Tomix.Cli/Tomix.Cli.csproj", workflow);
        Assert.Contains("--self-contained true", workflow);
        Assert.Contains("-p:PublishSingleFile=true", workflow);
        Assert.Contains("actions/upload-artifact@v7", workflow);
        Assert.Contains("name: pack tool", workflow);
    }
}
