using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Tomix.App.Update;

/// <summary>
/// The standalone-binary update mechanics: release asset naming, checksum verification
/// against the published <c>checksums.txt</c>, archive extraction, and the Windows-safe
/// swap of the running executable. Pure static pieces so each step is testable in isolation.
/// </summary>
public static class BinaryUpdater
{
    /// <summary>Release asset name for a runtime identifier, matching release.yml/install.sh.</summary>
    public static string AssetNameFor(string rid)
        => rid.StartsWith("win", StringComparison.OrdinalIgnoreCase)
            ? $"tx-{rid}.zip"
            : $"tx-{rid}.tar.gz";

    /// <summary>Verifies the asset's SHA-256 against the <c>sha256sum</c>-format checksums file.</summary>
    public static bool VerifyChecksum(byte[] asset, string checksumsText, string assetName)
    {
        var actual = Convert.ToHexStringLower(SHA256.HashData(asset));

        foreach (var line in checksumsText.Split('\n'))
        {
            // "hash  name" (sha256sum); tolerate the "hash *name" binary-mode variant.
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
                continue;

            var name = parts[1].TrimStart('*');
            if (string.Equals(name, assetName, StringComparison.Ordinal))
                return string.Equals(parts[0], actual, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Extracts the <c>tx</c>/<c>tx.exe</c> binary from a release archive
    /// (layout: <c>tx-&lt;rid&gt;/tx[.exe]</c>).
    /// </summary>
    public static byte[] ExtractBinary(byte[] archive, string rid)
    {
        var isZip = rid.StartsWith("win", StringComparison.OrdinalIgnoreCase);
        var binaryName = isZip ? "tx.exe" : "tx";
        var entryPath = $"tx-{rid}/{binaryName}";

        using var archiveStream = new MemoryStream(archive);

        if (isZip)
        {
            using var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read);
            var entry = zip.GetEntry(entryPath)
                ?? throw new InvalidDataException($"Archive does not contain '{entryPath}'.");
            using var entryStream = entry.Open();
            using var buffer = new MemoryStream();
            entryStream.CopyTo(buffer);
            return buffer.ToArray();
        }

        using var gzip = new GZipStream(archiveStream, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                continue;
            if (!string.Equals(entry.Name.TrimStart('.', '/'), entryPath, StringComparison.Ordinal))
                continue;

            using var buffer = new MemoryStream();
            entry.DataStream!.CopyTo(buffer);
            return buffer.ToArray();
        }

        throw new InvalidDataException($"Archive does not contain '{entryPath}'.");
    }

    /// <summary>
    /// Replaces the running executable. Renaming a running exe is legal on Windows (deleting
    /// is not), so the old binary moves aside to <c>.old</c> first; the new bytes land in a
    /// temp file in the same directory (same volume, so the final rename is atomic).
    /// On failure after the rename the old binary is moved back.
    /// </summary>
    public static void SwapInPlace(string processPath, byte[] newBinary)
    {
        var oldPath = processPath + ".old";
        TryDelete(oldPath); // stale leftover from a previous update on Windows

        var directory = Path.GetDirectoryName(processPath);
        if (string.IsNullOrEmpty(directory))
            throw new IOException($"Cannot resolve the directory of '{processPath}'.");

        var tempPath = Path.Combine(directory, $".tx-update-{Guid.NewGuid():N}");
        File.WriteAllBytes(tempPath, newBinary);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                tempPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        File.Move(processPath, oldPath);
        try
        {
            File.Move(tempPath, processPath);
        }
        catch
        {
            try { File.Move(oldPath, processPath); } catch { /* leave .old for manual recovery */ }
            TryDelete(tempPath);
            throw;
        }

        // Fails silently on Windows while the old exe is still the running process;
        // the stale .old is swept by the TryDelete above on the next update.
        TryDelete(oldPath);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best-effort */ }
    }
}
