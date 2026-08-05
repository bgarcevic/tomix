namespace Tomix.Tests.Support;

/// <summary>
/// The checked-in sample models under <c>samples/</c>, and throwaway copies of them for tests that
/// mutate or save. Tests must never open a sample in place — a failed save would dirty the working
/// tree — so anything that writes goes through <see cref="CopyToTemp"/> or <see cref="CopyTo"/>.
/// </summary>
public static class SampleModel
{
    /// <summary>The default sample: <c>samples/basic-tmdl</c> (3 tables, 12 columns, 2 relationships).</summary>
    public const string DefaultName = "basic-tmdl";

    /// <summary>Absolute path to <c>samples/basic-tmdl</c>.</summary>
    public static string Locate() => Locate(DefaultName);

    /// <summary>Absolute path to <paramref name="name"/> under <c>samples/</c>.</summary>
    /// <exception cref="InvalidOperationException">The sample is not checked in.</exception>
    public static string Locate(string name)
    {
        var path = System.IO.Path.Combine(RepoPaths.Samples, name);
        if (!Directory.Exists(path) && !File.Exists(path))
            throw new InvalidOperationException($"Sample not found: {path}");

        return path;
    }

    /// <summary>
    /// A throwaway copy of the sample whose <see cref="TempDir.Path"/> is the model folder itself.
    /// Dispose deletes the copy.
    /// </summary>
    public static TempDir CopyToTemp(string name = DefaultName)
    {
        var dir = new TempDir();
        try
        {
            CopyDirectory(Locate(name), dir.Path);
            return dir;
        }
        catch
        {
            // Never hand back a half-copied model, and never leak the directory behind it.
            dir.Dispose();
            throw;
        }
    }

    /// <summary>Copies the sample into <paramref name="parent"/> and returns the copy's path.</summary>
    public static string CopyTo(TempDir parent, string destinationName, string name = DefaultName)
    {
        var destination = parent.Combine(destinationName);
        CopyDirectory(Locate(name), destination);
        return destination;
    }

    /// <summary>Copies a single sample file (for example <c>basic-tmdl.bim</c>) into <paramref name="parent"/>.</summary>
    public static string CopyFileTo(TempDir parent, string destinationName, string name)
    {
        var destination = parent.Combine(destinationName);
        File.Copy(Locate(name), destination);
        return destination;
    }

    /// <summary>Recursively copies <paramref name="source"/> onto <paramref name="destination"/>.</summary>
    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = System.IO.Path.Combine(destination, System.IO.Path.GetRelativePath(source, file));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
