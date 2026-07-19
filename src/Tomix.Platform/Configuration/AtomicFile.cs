using System.Text;

namespace Tomix.Platform.Configuration;

/// <summary>
/// Crash-safe writes for shared state files (config, sessions, staging manifests, credentials):
/// the content is written to a sibling temp file, flushed to disk, then renamed over the target.
/// Readers never observe a truncated file — they see either the old or the new content.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
        => WriteAllBytes(path, Encoding.UTF8.GetBytes(contents));

    public static void WriteAllBytes(string path, byte[] bytes)
    {
        var temp = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best-effort cleanup */ }
            throw;
        }
    }
}
