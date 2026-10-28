namespace Tomix.Platform.Configuration;

/// <summary>
/// Finds files a project commits next to its models, the way git finds <c>.git</c>: by walking
/// up from a starting directory.
/// </summary>
public static class ProjectFiles
{
    /// <summary>
    /// The repository root marker. A <c>.git</c> folder, or the <c>.git</c> file a worktree or
    /// submodule has, ends the walk, so a file above the repository is never picked up.
    /// </summary>
    public const string RepositoryMarker = ".git";

    /// <summary>
    /// Every <paramref name="fileName"/> from <paramref name="startDirectory"/> up to and
    /// including the repository root (or the filesystem root outside a repository), nearest first.
    /// </summary>
    public static IReadOnlyList<string> FindUpward(string startDirectory, string fileName)
    {
        var found = new List<string>();
        for (var directory = new DirectoryInfo(Path.GetFullPath(startDirectory)); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
                found.Add(candidate);

            var marker = Path.Combine(directory.FullName, RepositoryMarker);
            if (Directory.Exists(marker) || File.Exists(marker))
                break;
        }

        return found;
    }
}
