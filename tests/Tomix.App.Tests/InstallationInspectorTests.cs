using Tomix.App.Update;
using Tomix.Core.Update;

namespace Tomix.App.Tests;

public sealed class InstallationInspectorTests
{
    private static InstallKind Detect(string? processPath, string baseDirectory, bool storeDirExists = false)
        => InstallationInspector.DetectFromPaths(processPath, baseDirectory, _ => storeDirExists);

    [Fact]
    public void NullProcessPath_IsUnknown()
    {
        Assert.Equal(InstallKind.Unknown, Detect(null, "/anywhere"));
    }

    [Theory]
    [InlineData("/repo/src/Tomix.Cli/bin/Debug/net10.0/")]
    [InlineData("/repo/src/Tomix.Cli/bin/Release/net10.0/")]
    [InlineData(@"C:\repo\src\Tomix.Cli\bin\Debug\net10.0\")]
    public void BuildOutputBaseDirectory_IsDevelopment(string baseDirectory)
    {
        Assert.Equal(InstallKind.Development, Detect("/usr/local/share/dotnet/dotnet", baseDirectory));
    }

    [Fact]
    public void ShimWithStoreDirectory_IsDotnetTool()
    {
        Assert.Equal(
            InstallKind.DotnetTool,
            Detect("/Users/u/.dotnet/tools/tx", "/Users/u/.dotnet/tools/.store/tomix.cli/0.2.0/tomix.cli/0.2.0/tools/net10.0/any/", storeDirExists: true));
    }

    [Fact]
    public void StoreBaseDirectoryWithoutShimSignal_IsDotnetTool()
    {
        Assert.Equal(
            InstallKind.DotnetTool,
            Detect("/opt/tools/tx", "/opt/tools/.store/tomix.cli/0.2.0/tomix.cli/0.2.0/tools/net10.0/any/"));
    }

    [Theory]
    [InlineData("/Users/u/.local/bin/tx", "/Users/u/.local/bin/")]
    [InlineData(@"C:\Users\u\bin\tx.exe", @"C:\Users\u\bin\")]
    [InlineData("/usr/local/bin/TX", "/usr/local/bin/")]
    public void StandaloneBinary_IsStandalone(string processPath, string baseDirectory)
    {
        Assert.Equal(InstallKind.Standalone, Detect(processPath, baseDirectory));
    }

    [Fact]
    public void UnrecognizedExecutableName_IsUnknown()
    {
        Assert.Equal(InstallKind.Unknown, Detect("/usr/bin/something-else", "/usr/bin/"));
    }
}
