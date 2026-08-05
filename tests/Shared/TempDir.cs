namespace Tomix.Tests.Support;

/// <summary>
/// A throwaway directory that exists for the lifetime of the <c>using</c> and is deleted
/// recursively on dispose. Replaces the hand-rolled
/// <c>Path.GetTempPath()</c> + <c>Directory.CreateDirectory</c> + <c>try</c>/<c>finally</c> +
/// <c>Directory.Delete</c> block; the directory exists as soon as the constructor returns, so a
/// test never has to remember the <c>CreateDirectory</c> call.
/// </summary>
/// <remarks>
/// Dispose deliberately does not swallow I/O failures. A directory that cannot be removed means a
/// handle is still open, and hiding that turns a leak into a silently passing test.
/// </remarks>
public sealed class TempDir : IDisposable
{
    /// <param name="prefix">
    /// Leading path segment, for recognizing strays in <c>$TMPDIR</c>. Keep the <c>tomix-</c>
    /// prefix so a stray directory is traceable back to this suite.
    /// </param>
    public TempDir(string prefix = "tomix-tests")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Path of a child of this directory. Creates nothing.</summary>
    public string Combine(params string[] parts)
        => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Creates a child directory and returns its path.</summary>
    public string CreateSubdirectory(params string[] parts)
    {
        var path = Combine(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Writes a file under this directory, creating any missing parents.</summary>
    public string WriteFile(string relativePath, string contents)
    {
        var path = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    public override string ToString() => Path;

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}
