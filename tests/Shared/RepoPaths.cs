namespace Tomix.Tests.Support;

/// <summary>
/// Locates the repository from a test run's output directory. The one place that knows how to get
/// from <c>bin/Debug/net10.0</c> back to the repo — replaces the per-assembly copies that either
/// re-implemented this search or hardcoded a <c>".."</c> depth (five levels, which silently breaks
/// the moment the target framework or configuration folder nesting changes).
/// </summary>
public static class RepoPaths
{
    /// <summary>The file that marks the repository root.</summary>
    private const string RootMarker = "Tomix.slnx";

    /// <summary>Absolute path to the repository root.</summary>
    public static string Root { get; } = FindRoot();

    /// <summary>Absolute path to the repository's <c>samples</c> folder.</summary>
    public static string Samples { get; } = System.IO.Path.Combine(Root, "samples");

    /// <summary>Absolute path to <paramref name="segments"/> under the repository root.</summary>
    public static string Combine(params string[] segments)
        => System.IO.Path.Combine([Root, .. segments]);

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, RootMarker)))
                return dir.FullName;
        }

        throw new InvalidOperationException(
            $"{RootMarker} not found above the test base directory ({AppContext.BaseDirectory}).");
    }
}
