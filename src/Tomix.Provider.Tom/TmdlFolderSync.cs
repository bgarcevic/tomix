using System.Text;

namespace Tomix.Provider.Tom;

/// <summary>
/// Mirrors a freshly serialized (staged) model folder onto its target while rewriting only the
/// files whose content changed (#224). Model folders are git artifacts, so an untouched file must
/// stay byte-identical — same bytes, same mtime — and a changed file keeps its existing line-ending
/// style and BOM, so a checkout under <c>core.autocrlf</c> never churns on EOLs alone. New files are
/// written as BOM-less UTF-8 with LF newlines on every OS. Stale <c>.tmdl</c> files (a removed or
/// renamed object) are deleted and emptied directories pruned; anything else in the folder — a
/// README, DAX test files, a nested <c>.git</c> — is not the serializer's and is left alone.
/// </summary>
internal static class TmdlFolderSync
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Case-insensitive file systems (Windows, macOS) see <c>Sales.tmdl</c> and <c>SALES.tmdl</c> as
    /// one file, so a case-only rename must be matched to its old file, not treated as add + delete.
    /// </summary>
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <param name="align">
    /// Applied to each staged <c>.tmdl</c> file's LF text together with the existing file's text
    /// (<c>null</c> when new) before comparing, so formatting conventions can follow the old file.
    /// </param>
    public static void Apply(string staging, string target, Func<string, string?, string> align)
    {
        Directory.CreateDirectory(target);

        var existing = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(target, f), PathComparer);
        var staged = new HashSet<string>(PathComparer);

        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(staging, file);
            staged.Add(relative);

            var destination = Path.Combine(target, relative);
            existing.TryGetValue(relative, out var current);
            var currentText = current is null ? null : File.ReadAllText(current);

            var text = ToLf(File.ReadAllText(file));
            if (IsTmdl(file))
                text = align(text, currentText is null ? null : ToLf(currentText));

            Write(destination, current, currentText, text);
        }

        foreach (var (relative, path) in existing)
        {
            if (IsTmdl(path) && !staged.Contains(relative))
                File.Delete(path);
        }

        PruneEmptyDirectories(target);
    }

    /// <summary>Writes <paramref name="contents"/> unless the file already holds it, modulo line endings.</summary>
    public static void WriteIfChanged(string path, string contents)
    {
        var current = File.Exists(path) ? path : null;
        Write(path, current, current is null ? null : File.ReadAllText(path), ToLf(contents));
    }

    /// <param name="current">The existing file (possibly differing from <paramref name="destination"/> in case only), or <c>null</c>.</param>
    /// <param name="lfText">The new content with LF newlines.</param>
    private static void Write(string destination, string? current, string? currentText, string lfText)
    {
        var sameName = current is not null && string.Equals(
            Path.GetFileName(current), Path.GetFileName(destination), StringComparison.Ordinal);

        if (currentText is not null && string.Equals(Comparable(currentText), Comparable(lfText), StringComparison.Ordinal))
        {
            if (!sameName)
            {
                // Via a temp name: a direct case-only move is a same-file move on case-insensitive systems.
                var temp = $"{destination}.tmp-{Guid.NewGuid():N}";
                File.Move(current!, temp);
                File.Move(temp, destination);
            }

            return;
        }

        var crlf = currentText?.Contains("\r\n", StringComparison.Ordinal) == true;
        var bom = current is not null && HasUtf8Bom(current);
        var bytes = Utf8NoBom.GetBytes(crlf ? lfText.Replace("\n", "\r\n", StringComparison.Ordinal) : lfText);
        if (bom)
            bytes = [.. Encoding.UTF8.Preamble, .. bytes];

        if (current is not null && !sameName)
            File.Delete(current);

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, bytes);
    }

    private static bool IsTmdl(string path) => path.EndsWith(".tmdl", StringComparison.OrdinalIgnoreCase);

    private static string ToLf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>
    /// Content identity for the skip-write check: line endings and the run of newlines at the end of
    /// the file carry no meaning (the serializer ends files with a blank line; editors often trim it).
    /// </summary>
    private static string Comparable(string text) => ToLf(text).TrimEnd('\n');

    private static bool HasUtf8Bom(string path)
    {
        Span<byte> head = stackalloc byte[3];
        using var stream = File.OpenRead(path);
        return stream.ReadAtLeast(head, 3, throwOnEndOfStream: false) == 3
               && head.SequenceEqual(Encoding.UTF8.Preamble);
    }

    private static void PruneEmptyDirectories(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            PruneEmptyDirectories(directory);
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
    }
}
