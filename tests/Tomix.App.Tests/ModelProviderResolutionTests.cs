using System.Runtime.Versioning;
using Tomix.App.Models;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

/// <summary>
/// Pins the unreadable-source probe in <see cref="ModelProviderResolution.ResolveSingleProvider"/>:
/// <see cref="IModelProvider.CanOpen"/> is a total predicate, so providers treat an unreadable
/// source as unowned — the probe must convert that into a <see cref="ModelLoadException"/> naming
/// the path instead of the misleading no-provider result, without misfiring on sources that are
/// merely unowned, nonexistent, or remote.
/// </summary>
public sealed class ModelProviderResolutionTests
{
    [Fact]
    public void ResolveSingleProvider_UnclaimedUnreadableFile_ThrowsModelLoadException()
    {
        if (!CanDropUnixReadPermission())
            return;

        // Deleting the enclosing directory needs write permission on the directory, not on the
        // file, so an unreadable file still cleans up with the rest of the TempDir.
        using var dir = new TempDir();
        var path = dir.WriteFile("Report.pbip", "{}");
        File.SetUnixFileMode(path, UnixFileMode.None);

        var providers = new IModelProvider[] { new StubProvider(claims: false) };

        var ex = Assert.Throws<ModelLoadException>(
            () => providers.ResolveSingleProvider(new ModelReference(path)));

        Assert.Contains(path, ex.Message);
    }

    [Fact]
    public void ResolveSingleProvider_UnclaimedUnreadableDirectory_ThrowsModelLoadException()
    {
        if (!CanDropUnixReadPermission())
            return;

        using var dir = new TempDir();
        var path = dir.CreateSubdirectory("Sales.SemanticModel");
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            var providers = new IModelProvider[] { new StubProvider(claims: false) };

            var ex = Assert.Throws<ModelLoadException>(
                () => providers.ResolveSingleProvider(new ModelReference(path)));

            Assert.Contains(path, ex.Message);
        }
        finally
        {
            // A directory the process cannot traverse cannot be removed recursively either, so
            // TempDir's dispose needs the mode restored first.
            RestoreOwnerAccess(path);
        }
    }

    [Fact]
    public void ResolveSingleProvider_FileUnderNonTraversableDirectory_ThrowsModelLoadException()
    {
        if (!CanDropUnixReadPermission())
            return;

        // File/Directory.Exists report false for a path the process cannot traverse to, so an
        // Exists-gated probe would silently skip it; the probe must open the path instead.
        using var dir = new TempDir();
        var path = dir.WriteFile(Path.Combine("locked", "Report.pbip"), "{}");
        var parent = dir.Combine("locked");
        File.SetUnixFileMode(parent, UnixFileMode.None);
        try
        {
            var providers = new IModelProvider[] { new StubProvider(claims: false) };

            var ex = Assert.Throws<ModelLoadException>(
                () => providers.ResolveSingleProvider(new ModelReference(path)));

            Assert.Contains(path, ex.Message);
        }
        finally
        {
            RestoreOwnerAccess(parent);
        }
    }

    [Fact]
    public void ResolveSingleProvider_UnclaimedReadableFile_ReturnsNull()
    {
        // The unreadable-source probe must not misfire on sources that are merely unowned.
        using var dir = new TempDir();
        var path = dir.WriteFile("notes.txt", "not a model");

        var providers = new IModelProvider[] { new StubProvider(claims: false) };

        Assert.Null(providers.ResolveSingleProvider(new ModelReference(path)));
    }

    [Fact]
    public void ResolveSingleProvider_UnclaimedNonexistentPath_ReturnsNull()
    {
        // FileNotFound from the open probe is the callers' no-provider case, not an
        // unreadable source.
        using var dir = new TempDir();
        var path = dir.Combine("absent.pbip");
        var providers = new IModelProvider[] { new StubProvider(claims: false) };

        Assert.Null(providers.ResolveSingleProvider(new ModelReference(path)));
    }

    [Fact]
    public void ResolveSingleProvider_UnclaimedRemoteReference_ReturnsNull()
    {
        var reference = new ModelReference("powerbi://api.powerbi.com/v1.0/myorg/Workspace", "Model");
        var providers = new IModelProvider[] { new StubProvider(claims: false) };

        Assert.Null(providers.ResolveSingleProvider(reference));
    }

    [Fact]
    public void ResolveSingleProvider_ClaimedUnreadableFile_ReturnsProviderWithoutProbing()
    {
        if (!CanDropUnixReadPermission())
            return;

        // A provider can claim a reference without reading it (e.g. by extension plus sibling
        // folders); a match must win over the unreadable-source probe.
        using var dir = new TempDir();
        var path = dir.WriteFile("Report.pbip", "{}");
        File.SetUnixFileMode(path, UnixFileMode.None);

        var expected = new StubProvider(claims: true);
        var providers = new IModelProvider[] { expected };

        Assert.Same(expected, providers.ResolveSingleProvider(new ModelReference(path)));
    }

    // Unix permission bits are the only way these tests can make a path unreadable; on Windows
    // (no unix modes) or as root (permission checks bypassed) the condition cannot be set up,
    // so the tests no-op there. CI and dev runs on macOS/Linux exercise them.
    [UnsupportedOSPlatformGuard("windows")]
    private static bool CanDropUnixReadPermission()
        => !OperatingSystem.IsWindows() && !Environment.IsPrivilegedProcess;

    [UnsupportedOSPlatform("windows")]
    private static void RestoreOwnerAccess(string path)
        => File.SetUnixFileMode(
            path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    private sealed class StubProvider(bool claims) : IModelProvider
    {
        public bool CanOpen(ModelReference reference) => claims;
        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
