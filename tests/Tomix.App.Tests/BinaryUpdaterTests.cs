using System.IO.Compression;
using System.Security.Cryptography;
using Tomix.App.Update;

namespace Tomix.App.Tests;

public sealed class BinaryUpdaterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tomix-binary-updater-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("linux-x64", "tx-linux-x64.tar.gz")]
    [InlineData("osx-arm64", "tx-osx-arm64.tar.gz")]
    [InlineData("win-x64", "tx-win-x64.zip")]
    [InlineData("win-arm64", "tx-win-arm64.zip")]
    public void AssetNameFor_MatchesReleaseNaming(string rid, string expected)
        => Assert.Equal(expected, BinaryUpdater.AssetNameFor(rid));

    [Fact]
    public void VerifyChecksum_AcceptsMatchingSha256()
    {
        var asset = "payload"u8.ToArray();
        var checksums =
            $"{new string('a', 64)}  tx-win-x64.zip\n" +
            $"{Convert.ToHexStringLower(SHA256.HashData(asset))}  tx-linux-x64.tar.gz\n";

        Assert.True(BinaryUpdater.VerifyChecksum(asset, checksums, "tx-linux-x64.tar.gz"));
    }

    [Theory]
    [InlineData("wrong hash")]
    [InlineData("missing entry")]
    public void VerifyChecksum_RejectsMismatchOrMissingEntry(string scenario)
    {
        var asset = "payload"u8.ToArray();
        var checksums = scenario == "wrong hash"
            ? $"{new string('0', 64)}  tx-linux-x64.tar.gz\n"
            : $"{Convert.ToHexStringLower(SHA256.HashData(asset))}  tx-win-x64.zip\n";

        Assert.False(BinaryUpdater.VerifyChecksum(asset, checksums, "tx-linux-x64.tar.gz"));
    }

    [Fact]
    public void ExtractBinary_ReadsTheBinaryFromATarGz()
    {
        var content = "unix-binary"u8.ToArray();
        var archive = UpdateApplyHandlerTests.TarGz("tx-osx-arm64/tx", content);

        Assert.Equal(content, BinaryUpdater.ExtractBinary(archive, "osx-arm64"));
    }

    [Fact]
    public void ExtractBinary_ReadsTheBinaryFromAZip()
    {
        var content = "windows-binary"u8.ToArray();
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entryStream = zip.CreateEntry("tx-win-x64/tx.exe").Open();
            entryStream.Write(content);
        }

        Assert.Equal(content, BinaryUpdater.ExtractBinary(buffer.ToArray(), "win-x64"));
    }

    [Fact]
    public void ExtractBinary_ThrowsWhenTheEntryIsMissing()
    {
        var archive = UpdateApplyHandlerTests.TarGz("tx-linux-x64/README.md", "docs"u8.ToArray());

        Assert.Throws<InvalidDataException>(() => BinaryUpdater.ExtractBinary(archive, "linux-x64"));
    }

    [Fact]
    public void SwapInPlace_ReplacesTheBinaryAndCleansUp()
    {
        var processPath = Path.Combine(_dir, "tx");
        File.WriteAllText(processPath, "old");
        File.WriteAllText(processPath + ".old", "stale-leftover");

        BinaryUpdater.SwapInPlace(processPath, "new"u8.ToArray());

        Assert.Equal("new", File.ReadAllText(processPath));
        Assert.False(File.Exists(processPath + ".old"));
        Assert.Empty(Directory.GetFiles(_dir, ".tx-update-*"));
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(processPath).HasFlag(UnixFileMode.UserExecute));
    }
}
