using System.Text;
using Tomix.App.Connect;

namespace Tomix.App.Tests;

public sealed class PowerBiDesktopDiscoveryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tomix-pbi-discovery-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>The bytes Power BI Desktop actually writes: UTF-16LE, no BOM.</summary>
    private static byte[] Utf16LeNoBom(string text) => Encoding.Unicode.GetBytes(text);

    /// <summary>Creates <c>&lt;root&gt;/AnalysisServicesWorkspace_&lt;id&gt;/Data/msmdsrv.port.txt</c>.</summary>
    private string WriteWorkspace(string root, string workspaceId, byte[] portFileBytes, bool inDataFolder = true)
    {
        var workspace = Path.Combine(_dir, root, $"AnalysisServicesWorkspace_{workspaceId}");
        var folder = inDataFolder ? Path.Combine(workspace, "Data") : workspace;
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "msmdsrv.port.txt"), portFileBytes);
        return Path.Combine(_dir, root);
    }

    /// <summary>Endpoints only, with liveness and report-name lookup stubbed out.</summary>
    private static IReadOnlyList<string> Discover(params string[] roots)
        => DiscoverInstances(roots).Select(i => i.Endpoint).ToList();

    private static IReadOnlyList<PowerBiDesktopInstance> DiscoverInstances(
        IReadOnlyList<string> roots,
        Func<IReadOnlyList<DesktopProcess>>? describeProcesses = null)
        => PowerBiDesktopDiscovery.DiscoverInstances(
            roots,
            PowerBiDesktopDiscovery.TryReadPort,
            isPortListening: null,
            describeProcesses ?? (() => []));

    // --- ParsePort -------------------------------------------------------------------------

    public static TheoryData<byte[], int> ValidPortFiles => new()
    {
        // The literal bytes read from a live Store-install port file (port 61696).
        { [0x36, 0x00, 0x31, 0x00, 0x36, 0x00, 0x39, 0x00, 0x36, 0x00], 61696 },
        // UTF-16LE with a BOM.
        { [0xFF, 0xFE, 0x36, 0x00, 0x31, 0x00, 0x36, 0x00, 0x39, 0x00, 0x36, 0x00], 61696 },
        // UTF-16BE with a BOM.
        { [0xFE, 0xFF, 0x00, 0x36, 0x00, 0x31, 0x00, 0x36, 0x00, 0x39, 0x00, 0x36], 61696 },
        // Plain ASCII/UTF-8.
        { [0x36, 0x31, 0x36, 0x39, 0x36], 61696 },
        // UTF-8 BOM.
        { [0xEF, 0xBB, 0xBF, 0x36, 0x31, 0x36, 0x39, 0x36], 61696 },
        // Trailing newlines, both encodings.
        { [0x36, 0x31, 0x36, 0x39, 0x36, 0x0D, 0x0A], 61696 },
        { [0x36, 0x00, 0x31, 0x00, 0x36, 0x00, 0x39, 0x00, 0x36, 0x00, 0x0D, 0x00, 0x0A, 0x00], 61696 },
        // Boundary ports.
        { [0x31], 1 },
        { [0x36, 0x35, 0x35, 0x33, 0x35], 65535 },
    };

    [Theory]
    [MemberData(nameof(ValidPortFiles))]
    public void ParsePort_DecodesEveryKnownEncoding(byte[] bytes, int expected)
        => Assert.Equal(expected, PowerBiDesktopDiscovery.ParsePort(bytes));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("70000")]   // in range for 5 digits, but above the max port
    [InlineData("123456")]  // too many digits
    [InlineData("   ")]
    [InlineData("port=61696")]
    public void ParsePort_RejectsNonPortContent(string text)
    {
        Assert.Null(PowerBiDesktopDiscovery.ParsePort(Encoding.ASCII.GetBytes(text)));
        Assert.Null(PowerBiDesktopDiscovery.ParsePort(Utf16LeNoBom(text)));
    }

    // --- DiscoverEndpoints ------------------------------------------------------------------

    [Fact]
    public void DiscoverEndpoints_FindsPortFileInWorkspaceDataFolder()
    {
        // The regression test for the original bug: a real Store-shaped layout whose port file
        // carries the real BOM-less UTF-16 bytes.
        var root = WriteWorkspace("store", "8f31072c", Utf16LeNoBom("61696"));

        Assert.Equal(["localhost:61696"], Discover(root));
    }

    [Fact]
    public void DiscoverEndpoints_FindsPortFileAtWorkspaceRoot()
    {
        var root = WriteWorkspace("legacy", "abc", Utf16LeNoBom("52001"), inDataFolder: false);

        Assert.Equal(["localhost:52001"], Discover(root));
    }

    [Fact]
    public void DiscoverEndpoints_ReturnsEveryLiveInstance()
    {
        WriteWorkspace("multi", "one", Utf16LeNoBom("51001"));
        var root = WriteWorkspace("multi", "two", Utf16LeNoBom("51002"));

        Assert.Equal(["localhost:51001", "localhost:51002"], Discover(root).Order());
    }

    [Fact]
    public void DiscoverEndpoints_DeduplicatesSamePortAcrossRoots()
    {
        var store = WriteWorkspace("store", "same", Utf16LeNoBom("61696"));
        var msi = WriteWorkspace("msi", "same", Utf16LeNoBom("61696"));

        Assert.Equal(["localhost:61696"], Discover(store, msi));
    }

    [Fact]
    public void DiscoverEndpoints_IgnoresMissingAndBlankRoots()
    {
        var missing = Path.Combine(_dir, "does-not-exist");

        Assert.Empty(Discover(missing, "", "   "));
    }

    [Fact]
    public void DiscoverEndpoints_SkipsUnparseablePortFile()
    {
        WriteWorkspace("mixed", "garbage", Encoding.ASCII.GetBytes("not-a-port"));
        var root = WriteWorkspace("mixed", "valid", Utf16LeNoBom("61696"));

        Assert.Equal(["localhost:61696"], Discover(root));
    }

    [Fact]
    public void DiscoverEndpoints_SkipsPortsWithNoActiveListener()
    {
        // A stale port file left behind by an exited instance must not be offered.
        var root = WriteWorkspace("stale", "gone", Utf16LeNoBom("61696"));

        Assert.Empty(PowerBiDesktopDiscovery.DiscoverInstances(
            [root], PowerBiDesktopDiscovery.TryReadPort, _ => false, () => []));
        Assert.Equal(
            ["localhost:61696"],
            PowerBiDesktopDiscovery.DiscoverInstances(
                [root], PowerBiDesktopDiscovery.TryReadPort, _ => true, () => [])
                .Select(i => i.Endpoint));
    }

    [Fact]
    public void TryReadPort_MissingFile_ReturnsNull()
        => Assert.Null(PowerBiDesktopDiscovery.TryReadPort(Path.Combine(_dir, "nope.txt")));

    [Fact]
    public void TryReadPort_DirectoryPath_ReturnsNull()
        => Assert.Null(PowerBiDesktopDiscovery.TryReadPort(_dir));

    // --- Report-name labelling ---------------------------------------------------------------

    [Fact]
    public void DiscoverInstances_LabelsEndpointWithReportNameFromMatchingProcess()
    {
        var root = WriteWorkspace("store", "8f31072c", Utf16LeNoBom("61696"));
        var dataDirectory = Path.Combine(root, "AnalysisServicesWorkspace_8f31072c", "Data");

        var instance = Assert.Single(DiscoverInstances(
            [root],
            () => [new DesktopProcess(dataDirectory, "B4 - Bonustimer")]));

        Assert.Equal("localhost:61696", instance.Endpoint);
        Assert.Equal("B4 - Bonustimer", instance.ReportName);
    }

    [Fact]
    public void DiscoverInstances_JoinsOnDataDirectoryIgnoringTrailingSeparator()
    {
        var root = WriteWorkspace("store", "abc", Utf16LeNoBom("61696"));
        var withSeparator = Path.Combine(root, "AnalysisServicesWorkspace_abc", "Data")
            + Path.DirectorySeparatorChar;

        Assert.Equal(
            "Sales Overview",
            Assert.Single(DiscoverInstances([root], () => [new DesktopProcess(withSeparator, "Sales Overview")])).ReportName);
    }

    [Fact]
    public void DiscoverInstances_UnmatchedOrMissingProcess_LeavesReportNameNull()
    {
        // Report names are a nicety; an instance whose process cannot be described is still usable.
        var root = WriteWorkspace("store", "abc", Utf16LeNoBom("61696"));

        Assert.Null(Assert.Single(DiscoverInstances([root], () => [])).ReportName);
        Assert.Null(Assert.Single(DiscoverInstances(
            [root],
            () => [new DesktopProcess(Path.Combine(_dir, "somewhere", "else"), "Other Report")])).ReportName);
    }

    [Theory]
    // The literal command line of a live Store-install engine.
    [InlineData(
        @"""C:\Program Files\WindowsApps\Microsoft.MicrosoftPowerBIDesktop_2.156.951.0_x64__8wekyb3d8bbwe\bin\msmdsrv.exe"" -c -n AnalysisServicesWorkspace_8f31072c -s ""C:\Users\me\Microsoft\Power BI Desktop Store App\AnalysisServicesWorkspaces\AnalysisServicesWorkspace_8f31072c\Data""",
        @"C:\Users\me\Microsoft\Power BI Desktop Store App\AnalysisServicesWorkspaces\AnalysisServicesWorkspace_8f31072c\Data")]
    // Unquoted, i.e. a path without spaces.
    [InlineData(@"msmdsrv.exe -c -n Workspace_1 -s C:\ws\Data", @"C:\ws\Data")]
    [InlineData(@"msmdsrv.exe -s C:\ws\Data -c", @"C:\ws\Data")]
    public void DataDirectoryFrom_ExtractsTheWorkspacePath(string commandLine, string expected)
        => Assert.Equal(expected, PowerBiDesktopProcesses.DataDirectoryFrom(commandLine));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("msmdsrv.exe -c -n Workspace_1")]         // no -s at all
    [InlineData(@"msmdsrv.exe -c -s ""C:\unterminated")]  // opening quote never closed
    [InlineData("msmdsrv.exe -c -s ")]                    // -s with nothing after it
    public void DataDirectoryFrom_ReturnsNullWhenNoPathIsPresent(string? commandLine)
        => Assert.Null(PowerBiDesktopProcesses.DataDirectoryFrom(commandLine));

    // --- WorkspaceRoots ---------------------------------------------------------------------

    [Theory]
    // Microsoft Store, verified against a live instance. Note the user-profile base.
    [InlineData(Environment.SpecialFolder.UserProfile, "Microsoft", "Power BI Desktop Store App", "AnalysisServicesWorkspaces")]
    // Reported Store variant under LOCALAPPDATA.
    [InlineData(Environment.SpecialFolder.LocalApplicationData, "Microsoft", "Power BI Desktop Store App", "AnalysisServicesWorkspaces")]
    // MSI / Download Center install.
    [InlineData(Environment.SpecialFolder.LocalApplicationData, "Microsoft", "Power BI Desktop", "AnalysisServicesWorkspaces")]
    // Legacy Store packaging.
    [InlineData(Environment.SpecialFolder.LocalApplicationData, "Packages", "Microsoft.MicrosoftPowerBIDesktop_8wekyb3d8bbwe", "LocalCache", "Microsoft", "Power BI Desktop", "AnalysisServicesWorkspaces")]
    public void WorkspaceRoots_CoversEveryKnownInstallVariant(
        Environment.SpecialFolder baseFolder,
        params string[] segments)
    {
        // Compare on path segments rather than a rebuilt literal, so this asserts the shape of the
        // probed roots instead of restating the production expression.
        var expected = new[] { Environment.GetFolderPath(baseFolder) }.Concat(segments);

        var roots = PowerBiDesktopDiscovery.WorkspaceRoots()
            .Select(root => root.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        Assert.Contains(roots, root => root.SequenceEqual(
            expected.SelectMany(segment => segment.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))));
    }
}
