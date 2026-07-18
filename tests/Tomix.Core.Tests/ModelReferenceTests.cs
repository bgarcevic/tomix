using Tomix.Core.Models;

namespace Tomix.Core.Tests;

public sealed class ModelReferenceTests
{
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

    [Fact]
    public void NormalizeEndpoint_PrefixesBareWorkspaceNames()
        => Assert.Equal(
            "powerbi://api.powerbi.com/v1.0/myorg/MyWorkspace",
            ModelReference.NormalizeEndpoint("MyWorkspace"));

    [Fact]
    public void NormalizeEndpoint_UnescapesBareNamesFromBrowserUrls()
        => Assert.Equal(
            "powerbi://api.powerbi.com/v1.0/myorg/sandbox bkg",
            ModelReference.NormalizeEndpoint("sandbox%20bkg"));

    [Theory]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/Sales%20Archive")]
    [InlineData("asazure://region.asazure.windows.net/server")]
    [InlineData("localhost:12345")]
    public void NormalizeEndpoint_IsIdempotentForFormedEndpoints(string endpoint)
        => Assert.Equal(endpoint, ModelReference.NormalizeEndpoint(endpoint));

    [Fact]
    public void LocalPath_IsNeitherRemoteNorLocalInstance()
    {
        var reference = new ModelReference("/path/to/model");

        Assert.True(reference.IsLocalPath);
        Assert.False(reference.IsRemote);
        Assert.False(reference.IsLocalInstance);
    }
}
