using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Tomix.App.Update;
using Tomix.Core.Update;

namespace Tomix.App.Tests;

public sealed class UpdateApplyHandlerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tomix-update-apply-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public string? FileName;
        public IReadOnlyList<string>? Arguments;
        public ProcessRunResult Result { get; init; } = new(0, "", "");

        public Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            FileName = fileName;
            Arguments = arguments;
            return Task.FromResult(Result);
        }
    }

    private static UpdateApplyRequest Request(
        InstallKind kind,
        string? processPath = null,
        string target = "0.3.0",
        string rid = "linux-x64")
        => new(
            CurrentVersion: "0.1.0",
            TargetVersion: target,
            Kind: kind,
            ProcessPath: processPath,
            RuntimeIdentifier: rid);

    [Fact]
    public async Task DotnetTool_RunsDotnetToolUpdateWithPinnedVersion()
    {
        var runner = new RecordingProcessRunner();
        var handler = new UpdateApplyHandler(new FakeReleaseSource(), runner);

        var result = await handler.HandleAsync(Request(InstallKind.DotnetTool), CancellationToken.None);

        Assert.NotNull(result.Data);
        Assert.Equal("dotnet", runner.FileName);
        Assert.Equal(["tool", "update", "-g", "Tomix.Cli", "--version", "0.3.0"], runner.Arguments);
        Assert.Equal("dotnet-tool", result.Data.Method);
        Assert.Equal("0.3.0", result.Data.NewVersion);
    }

    [Fact]
    public async Task DotnetTool_NonZeroExit_FailsWithToolFailed()
    {
        var runner = new RecordingProcessRunner { Result = new ProcessRunResult(1, "", "access denied") };
        var handler = new UpdateApplyHandler(new FakeReleaseSource(), runner);

        var result = await handler.HandleAsync(Request(InstallKind.DotnetTool), CancellationToken.None);

        Assert.Null(result.Data);
        Assert.Equal(1, result.ExitCode);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("TOMIX_UPDATE_TOOL_FAILED", diagnostic.Code);
        Assert.Contains("access denied", diagnostic.Message);
    }

    [Theory]
    [InlineData(InstallKind.Development)]
    [InlineData(InstallKind.Unknown)]
    public async Task NonUpdatableInstall_FailsWithUnsupportedInstall(InstallKind kind)
    {
        var handler = new UpdateApplyHandler(new FakeReleaseSource(), new RecordingProcessRunner());

        var result = await handler.HandleAsync(Request(kind), CancellationToken.None);

        Assert.Null(result.Data);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TOMIX_UPDATE_UNSUPPORTED_INSTALL");
    }

    [Fact]
    public async Task Standalone_DownloadsVerifiesAndSwapsTheBinary()
    {
        var processPath = Path.Combine(_dir, "tx");
        File.WriteAllText(processPath, "old-binary");
        var newBinary = "new-binary"u8.ToArray();
        var asset = TarGz("tx-linux-x64/tx", newBinary);

        var source = new FakeReleaseSource
        {
            Assets = { ["0.3.0/tx-linux-x64.tar.gz"] = asset },
            ChecksumsText = $"{Convert.ToHexStringLower(SHA256.HashData(asset))}  tx-linux-x64.tar.gz\n"
        };
        var handler = new UpdateApplyHandler(source, new RecordingProcessRunner());

        var result = await handler.HandleAsync(Request(InstallKind.Standalone, processPath), CancellationToken.None);

        Assert.NotNull(result.Data);
        Assert.Equal("binary-swap", result.Data.Method);
        Assert.Equal(newBinary, File.ReadAllBytes(processPath));
        Assert.False(File.Exists(processPath + ".old"));
    }

    [Fact]
    public async Task Standalone_ChecksumMismatch_FailsAndLeavesTheBinaryUntouched()
    {
        var processPath = Path.Combine(_dir, "tx");
        File.WriteAllText(processPath, "old-binary");
        var asset = TarGz("tx-linux-x64/tx", "evil"u8.ToArray());

        var source = new FakeReleaseSource
        {
            Assets = { ["0.3.0/tx-linux-x64.tar.gz"] = asset },
            ChecksumsText = $"{new string('0', 64)}  tx-linux-x64.tar.gz\n"
        };
        var handler = new UpdateApplyHandler(source, new RecordingProcessRunner());

        var result = await handler.HandleAsync(Request(InstallKind.Standalone, processPath), CancellationToken.None);

        Assert.Null(result.Data);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TOMIX_UPDATE_CHECKSUM_MISMATCH");
        Assert.Equal("old-binary", File.ReadAllText(processPath));
    }

    [Fact]
    public async Task Standalone_MissingAsset_FailsWithDownloadFailed()
    {
        var processPath = Path.Combine(_dir, "tx");
        File.WriteAllText(processPath, "old-binary");
        var handler = new UpdateApplyHandler(new FakeReleaseSource(), new RecordingProcessRunner());

        var result = await handler.HandleAsync(Request(InstallKind.Standalone, processPath), CancellationToken.None);

        Assert.Null(result.Data);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TOMIX_UPDATE_DOWNLOAD_FAILED");
        Assert.Equal("old-binary", File.ReadAllText(processPath));
    }

    [Fact]
    public async Task Standalone_WithoutProcessPath_FailsWithApplyFailed()
    {
        var handler = new UpdateApplyHandler(new FakeReleaseSource(), new RecordingProcessRunner());

        var result = await handler.HandleAsync(Request(InstallKind.Standalone, processPath: null), CancellationToken.None);

        Assert.Null(result.Data);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TOMIX_UPDATE_APPLY_FAILED");
    }

    internal static byte[] TarGz(string entryPath, byte[] content)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, entryPath)
            {
                DataStream = new MemoryStream(content)
            };
            tar.WriteEntry(entry);
        }

        return buffer.ToArray();
    }
}
