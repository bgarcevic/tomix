namespace Tomix.Provider.Tmdl.Tests;

/// <summary>
/// Shared helpers: locating the checked-in <c>samples/basic-tmdl</c> model and creating
/// self-cleaning temp directories for save round-trips.
/// </summary>
internal static class TestSupport
{
    /// <summary>Absolute path to the repo's <c>samples/basic-tmdl</c> folder.</summary>
    public static string SampleTmdlFolder { get; } = FindSampleTmdlFolder();

    private static string FindSampleTmdlFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "samples", "basic-tmdl");
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new InvalidOperationException("samples/basic-tmdl not found above test base directory.");
    }

    public static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(dest, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "tomix-tmdl-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch
        {
            // Best-effort cleanup; leaked temp dirs must not fail the test run.
        }
    }
}
