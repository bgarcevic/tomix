using Tomix.Core.Models;

namespace Tomix.Core.Tests;

/// <summary>
/// Endpoint classification and normalization for <see cref="ModelReference"/> — both the static
/// predicates and the instance properties derived from them.
/// </summary>
public sealed class ModelReferenceTests
{
    // ── Static predicates ───────────────────────────────────────────────────

    [Theory]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/ws")]
    [InlineData("asazure://region.asazure.windows.net/server")]
    [InlineData("link://onelake")]
    [InlineData("localhost:12345")]
    [InlineData("127.0.0.1:12345")]
    public void IsRemoteEndpoint_RecognizesXmlaSchemesAndLocalInstances(string value)
        => Assert.True(ModelReference.IsRemoteEndpoint(value));

    [Theory]
    [InlineData("/path/to/model")]
    [InlineData(@"C:\models\model.bim")]
    [InlineData(@"samples\basic-tmdl")]
    [InlineData("MyWorkspace")]
    [InlineData("")]
    [InlineData(null)]
    public void IsRemoteEndpoint_RejectsPathsBareNamesAndEmpty(string? value)
        => Assert.False(ModelReference.IsRemoteEndpoint(value));

    [Theory]
    [InlineData("localhost:12345", true)]
    [InlineData("127.0.0.1:8080", true)]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/ws", false)]
    [InlineData("/path/to/model", false)]
    public void IsLocalInstanceEndpoint_MatchesOnlyDesktopInstances(string value, bool expected)
        => Assert.Equal(expected, ModelReference.IsLocalInstanceEndpoint(value));

    // ── Instance classification ─────────────────────────────────────────────

    [Theory]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/Workspace", true)]
    [InlineData("asazure://westeurope.asazure.windows.net/server", true)]
    [InlineData("localhost:52123", true)]
    [InlineData("127.0.0.1:52123", true)]
    [InlineData(@"samples\basic-tmdl", false)]
    [InlineData(@"C:\models\model.bim", false)]
    [InlineData("", false)]
    public void IsRemote_FollowsEndpointClassification(string value, bool expected)
        => Assert.Equal(expected, new ModelReference(value).IsRemote);

    [Theory]
    [InlineData("localhost:52123", true)]
    [InlineData("127.0.0.1:52123", true)]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/Workspace", false)]
    public void IsLocalInstance_DistinguishesDesktop(string value, bool expected)
        => Assert.Equal(expected, new ModelReference(value).IsLocalInstance);

    [Fact]
    public void LocalPath_IsNeitherRemoteNorLocalInstance()
    {
        var reference = new ModelReference("/path/to/model");

        Assert.True(reference.IsLocalPath);
        Assert.False(reference.IsRemote);
        Assert.False(reference.IsLocalInstance);
    }

    [Fact]
    public void Remote_CarriesDatabase()
    {
        var reference = ModelReference.Remote("powerbi://api.powerbi.com/v1.0/myorg/Workspace", "Sales");

        Assert.True(reference.IsRemote);
        Assert.Equal("Sales", reference.Database);
        Assert.False(reference.IsLocalPath);
    }

    // ── Normalization ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("MyWorkspace", "powerbi://api.powerbi.com/v1.0/myorg/MyWorkspace")]
    [InlineData("My Workspace", "powerbi://api.powerbi.com/v1.0/myorg/My Workspace")]
    // Bare names arriving from a browser URL are percent-decoded.
    [InlineData("sandbox%20bkg", "powerbi://api.powerbi.com/v1.0/myorg/sandbox bkg")]
    public void NormalizeEndpoint_ExpandsBareWorkspaceName(string input, string expected)
    {
        var normalized = ModelReference.NormalizeEndpoint(input);

        Assert.Equal(expected, normalized);
        Assert.True(ModelReference.IsRemoteEndpoint(normalized));
    }

    [Theory]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/Workspace")]
    [InlineData("asazure://region.asazure.windows.net/server")]
    [InlineData("link://resource/")]
    [InlineData("localhost:12345")]
    [InlineData("127.0.0.1:52123")]
    [InlineData("https://example.com")] // non-XMLA scheme preserved as-is (not a bare name)
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/Sales%20Archive")] // endpoint %XX taken literally (idempotent)
    public void NormalizeEndpoint_PassesExistingEndpointsThrough(string input)
        => Assert.Equal(input, ModelReference.NormalizeEndpoint(input));

    [Fact]
    public void NormalizeEndpoint_IsIdempotent_ForBrowserEscapedWorkspaceName()
    {
        // "Sales%2520Archive" is the browser-escaped form of the literal workspace name
        // "Sales%20Archive". One decode resolves it; a second pass (applied at connect time
        // by TomModelDeployer.ResolveEndpoint) must not turn "%20" into a space.
        var once = ModelReference.NormalizeEndpoint("Sales%2520Archive");
        var twice = ModelReference.NormalizeEndpoint(once);

        Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/Sales%20Archive", once);
        Assert.Equal(once, twice);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void NormalizeEndpoint_ReturnsEmptyForBlank(string? input, string expected)
        => Assert.Equal(expected, ModelReference.NormalizeEndpoint(input));
}
