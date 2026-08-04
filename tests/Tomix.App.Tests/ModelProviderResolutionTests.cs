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

        var path = Path.Combine(Path.GetTempPath(), $"tomix-unreadable-{Guid.NewGuid():N}.pbip");
        File.WriteAllText(path, "{}");
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
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolveSingleProvider_UnclaimedUnreadableDirectory_ThrowsModelLoadException()
    {
        if (!CanDropUnixReadPermission())
            return;

        var path = Path.Combine(Path.GetTempPath(), $"tomix-unreadable-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
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
            File.SetUnixFileMode(
                path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(path);
        }
    }

    [Fact]
    public void ResolveSingleProvider_FileUnderNonTraversableDirectory_ThrowsModelLoadException()
    {
        if (!CanDropUnixReadPermission())
            return;

        // File/Directory.Exists report false for a path the process cannot traverse to, so an
        // Exists-gated probe would silently skip it; the probe must open the path instead.
        var parent = Path.Combine(Path.GetTempPath(), $"tomix-nontraversable-{Guid.NewGuid():N}");
        Directory.CreateDirectory(parent);
        var path = Path.Combine(parent, "Report.pbip");
        File.WriteAllText(path, "{}");
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
            File.SetUnixFileMode(
                parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void ResolveSingleProvider_UnclaimedReadableFile_ReturnsNull()
    {
        // The unreadable-source probe must not misfire on sources that are merely unowned.
        var path = Path.Combine(Path.GetTempPath(), $"tomix-readable-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "not a model");
        try
        {
            var providers = new IModelProvider[] { new StubProvider(claims: false) };

            Assert.Null(providers.ResolveSingleProvider(new ModelReference(path)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolveSingleProvider_UnclaimedNonexistentPath_ReturnsNull()
    {
        // FileNotFound from the open probe is the callers' no-provider case, not an
        // unreadable source.
        var path = Path.Combine(Path.GetTempPath(), $"tomix-missing-{Guid.NewGuid():N}.pbip");
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
        var path = Path.Combine(Path.GetTempPath(), $"tomix-unreadable-{Guid.NewGuid():N}.pbip");
        File.WriteAllText(path, "{}");
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            var expected = new StubProvider(claims: true);
            var providers = new IModelProvider[] { expected };

            Assert.Same(expected, providers.ResolveSingleProvider(new ModelReference(path)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Unix permission bits are the only way these tests can make a path unreadable; on Windows
    // (no unix modes) or as root (permission checks bypassed) the condition cannot be set up,
    // so the tests no-op there. CI and dev runs on macOS/Linux exercise them.
    [UnsupportedOSPlatformGuard("windows")]
    private static bool CanDropUnixReadPermission()
        => !OperatingSystem.IsWindows() && !Environment.IsPrivilegedProcess;

    private sealed class StubProvider(bool claims) : IModelProvider
    {
        public bool CanOpen(ModelReference reference) => claims;
        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
