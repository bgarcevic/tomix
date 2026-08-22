using Tomix.Core.Models;

namespace Tomix.Core.Tests;

/// <summary>
/// Guards the remote connect timeout and the one place it is applied.
/// </summary>
/// <remarks>
/// The wall-clock behaviour can only be confirmed against a real unreachable endpoint — AMO owns
/// what <c>Connect Timeout</c> does — so these pin the half that is checkable offline: the key is
/// present for remote endpoints, absent for a local Desktop instance, and no other source file
/// hand-builds a client connection string that could miss it.
/// </remarks>
public sealed class XmlaConnectionStringTests
{
    [Theory]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/Sales")]
    [InlineData("asazure://westeurope.asazure.windows.net/myserver")]
    [InlineData("MyWorkspace")]
    public void RemoteEndpoint_CarriesConnectTimeout(string endpoint)
        => Assert.Contains(
            $"Connect Timeout={XmlaConnectionString.RemoteConnectTimeoutSeconds}",
            XmlaConnectionString.Build(endpoint),
            StringComparison.Ordinal);

    [Theory]
    [InlineData("localhost:59962")]
    [InlineData("127.0.0.1:59962")]
    public void LocalDesktopInstance_OmitsConnectTimeout(string endpoint)
    {
        Assert.True(ModelReference.IsLocalInstanceEndpoint(endpoint), $"'{endpoint}' should be a local instance.");
        Assert.DoesNotContain("Connect Timeout", XmlaConnectionString.Build(endpoint), StringComparison.Ordinal);
    }

    [Fact]
    public void Database_PrecedesTheTimeout()
    {
        var connectionString = XmlaConnectionString.Build("powerbi://api.powerbi.com/v1.0/myorg/Sales", "SalesModel");

        Assert.Equal(
            "Data Source=powerbi://api.powerbi.com/v1.0/myorg/Sales;Initial Catalog=SalesModel;Connect Timeout=30",
            connectionString);
    }

    [Fact]
    public void BareWorkspace_IsNormalizedToAnEndpoint()
        => Assert.StartsWith(
            "Data Source=powerbi://api.powerbi.com/v1.0/myorg/MyWorkspace",
            XmlaConnectionString.Build("MyWorkspace"),
            StringComparison.Ordinal);

    /// <summary>
    /// Files allowed to contain a <c>Data Source=</c> literal, and why.
    /// </summary>
    private static readonly Dictionary<string, string> ConnectionStringAuthors = new(StringComparer.Ordinal)
    {
        ["Tomix.Core/Models/XmlaConnectionString.cs"] = "the builder itself",
        ["Tomix.Provider.Tom/TomObjectAdder.cs"] = "writes a partition's data-source definition into the model, not a client connection",
    };

    /// <summary>
    /// Every file that <em>builds</em> a client connection string, and so must route through the
    /// builder. A positive assertion, because <c>deploy</c> and <c>vertipaq</c> cannot be reached
    /// by a unit test — nothing else would notice them being rewritten to connect their own way.
    /// </summary>
    /// <remarks>
    /// Not the same set as "files that open a connection": <c>TomModelQueryExecutor</c> and
    /// <c>TomQueryTraceSink</c> both connect, but from a string handed to them by
    /// <c>TomServerModelProvider</c>, so they inherit the timeout and have nothing to assert here.
    /// </remarks>
    private static readonly string[] ClientConnectionFiles =
    [
        "Tomix.Provider.Tom/TomServerModelProvider.cs",
        "Tomix.Provider.Tom/TomModelDeployer.cs",
        "Tomix.Provider.Vpax/VpaxVertipaqAnalyzer.cs",
    ];

    /// <summary>
    /// Matches a connection string being assembled by hand, tolerating the spacing the keyword
    /// legally allows — the plain-substring version of this guard read as protection while
    /// <c>"Data Source ="</c> walked straight past it.
    /// </summary>
    /// <remarks>
    /// Case-insensitive, because connection-string keywords are: <c>data source=</c> opens exactly
    /// the same connection as <c>Data Source=</c>. The whitespace between the two words is required
    /// rather than optional, though — <c>DataSource</c> is the TOM type and property name, and
    /// matching that flags every file that merely removes a data source from a model.
    /// </remarks>
    private static readonly System.Text.RegularExpressions.Regex HandBuiltConnectionString =
        new(@"Data\s+Source\s*=",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant
            | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// The timeout is only worth anything if every client connection goes through the builder.
    /// It first shipped applied at one call site, which left <c>deploy</c> and <c>vertipaq</c>
    /// connecting with no timeout at all — a hand-built string is how that regresses.
    /// </summary>
    [Fact]
    public void OnlyTheBuilder_ConstructsClientConnectionStrings()
    {
        var offenders = SourceFiles()
            .Where(file => HandBuiltConnectionString.IsMatch(File.ReadAllText(file.Path)))
            .Select(file => file.Relative)
            .Where(relative => !ConnectionStringAuthors.ContainsKey(relative))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"These files build a connection string by hand and so cannot carry the remote connect "
            + $"timeout: {string.Join(", ", offenders)}. Use {nameof(XmlaConnectionString)}.{nameof(XmlaConnectionString.Build)} "
            + $"instead, or add the file to {nameof(ConnectionStringAuthors)} with the reason it is not a client connection.");
    }

    /// <summary>
    /// The other direction: a new connection path can evade the scan above entirely by building
    /// its string somewhere else and passing it in, so the files known to connect must be shown
    /// to reach the builder rather than merely to lack a literal.
    /// </summary>
    [Theory]
    [MemberData(nameof(ClientConnectionFileCases))]
    public void EveryClientConnectionPath_RoutesThroughTheBuilder(string relativePath)
    {
        var source = File.ReadAllText(RepoPaths.Combine("src", relativePath));

        Assert.True(
            source.Contains($"{nameof(XmlaConnectionString)}.{nameof(XmlaConnectionString.Build)}", StringComparison.Ordinal),
            $"{relativePath} opens a client connection but no longer calls "
            + $"{nameof(XmlaConnectionString)}.{nameof(XmlaConnectionString.Build)}, so its connect is uncapped. "
            + "If it genuinely stopped connecting, drop it from ClientConnectionFiles.");
    }

    public static TheoryData<string> ClientConnectionFileCases()
    {
        var data = new TheoryData<string>();
        foreach (var file in ClientConnectionFiles)
            data.Add(file);
        return data;
    }

    private static IEnumerable<(string Path, string Relative)> SourceFiles()
        => Directory
            .EnumerateFiles(RepoPaths.Combine("src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (path, Path.GetRelativePath(RepoPaths.Combine("src"), path).Replace(Path.DirectorySeparatorChar, '/')));
}
