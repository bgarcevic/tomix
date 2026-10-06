using System.Security.Cryptography;
using System.Text;

namespace Tomix.Provider.Tom;

/// <summary>
/// The content of a TMDL folder or model file, as a hash, and a watcher that reports when it may
/// have changed: how a live session notices edits made outside it (#351). A TMDL folder counts
/// its <c>.tmdl</c> files only, by relative path and content, so a save that rewrites identical
/// bytes or a tool that touches other files does not count as a change.
/// </summary>
internal static class SourceFingerprint
{
    /// <summary>A hash of what <paramref name="path"/> holds; <c>missing</c> when it is gone.</summary>
    public static string Of(string path)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (Directory.Exists(path))
        {
            foreach (var file in Directory.EnumerateFiles(path, "*.tmdl", SearchOption.AllDirectories)
                         .Select(file => (Full: file, Relative: Path.GetRelativePath(path, file).Replace('\\', '/')))
                         .OrderBy(file => file.Relative, StringComparer.Ordinal))
            {
                if (ReadOrNull(file.Full) is not { } content)
                    continue;
                hash.AppendData(Encoding.UTF8.GetBytes(file.Relative + "\n" + content.Length + "\n"));
                hash.AppendData(content);
            }
        }
        else if (ReadOrNull(path) is { } content)
        {
            hash.AppendData(content);
        }
        else
        {
            return "missing";
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>Watches <paramref name="path"/>, a folder (and its subfolders) or a file; <c>null</c>
    /// when its folder does not exist.</summary>
    public static IDisposable? Watch(string path, Action changed)
    {
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return null;

        var watcher = Directory.Exists(path)
            ? new FileSystemWatcher(path, "*.tmdl") { IncludeSubdirectories = true }
            : new FileSystemWatcher(folder, Path.GetFileName(path));
        watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
        FileSystemEventHandler onChange = (_, _) => changed();
        watcher.Changed += onChange;
        watcher.Created += onChange;
        watcher.Deleted += onChange;
        watcher.Renamed += (_, _) => changed();
        // A lost buffer or a removed folder: the caller checks the fingerprint, which tells.
        watcher.Error += (_, _) => changed();
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private static byte[]? ReadOrNull(string file)
    {
        try
        {
            return File.ReadAllBytes(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
