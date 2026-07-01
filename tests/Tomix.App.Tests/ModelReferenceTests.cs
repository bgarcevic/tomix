using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class ModelReferenceTests
{
    [Theory]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/Workspace")]
    [InlineData("asazure://westeurope.asazure.windows.net/server")]
    [InlineData("localhost:52123")]
    [InlineData("127.0.0.1:52123")]
    public void IsRemote_True_ForEndpoints(string value)
        => Assert.True(new ModelReference(value).IsRemote);

    [Theory]
    [InlineData("samples\\basic-tmdl")]
    [InlineData("C:\\models\\model.bim")]
    [InlineData("")]
    public void IsRemote_False_ForLocalPaths(string value)
        => Assert.False(new ModelReference(value).IsRemote);

    [Theory]
    [InlineData("localhost:52123", true)]
    [InlineData("127.0.0.1:52123", true)]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/Workspace", false)]
    public void IsLocalInstance_DistinguishesDesktop(string value, bool expected)
        => Assert.Equal(expected, new ModelReference(value).IsLocalInstance);

    [Fact]
    public void Remote_CarriesDatabase()
    {
        var reference = ModelReference.Remote("powerbi://api.powerbi.com/v1.0/myorg/Workspace", "Sales");

        Assert.True(reference.IsRemote);
        Assert.Equal("Sales", reference.Database);
        Assert.False(reference.IsLocalPath);
    }

    [Theory]
    [InlineData("MyWorkspace", "powerbi://api.powerbi.com/v1.0/myorg/MyWorkspace")]
    [InlineData("My Workspace", "powerbi://api.powerbi.com/v1.0/myorg/My Workspace")]
    public void NormalizeEndpoint_ExpandsBareWorkspaceName(string input, string expected)
    {
        var normalized = ModelReference.NormalizeEndpoint(input);

        Assert.Equal(expected, normalized);
        Assert.True(ModelReference.IsRemoteEndpoint(normalized));
    }

    [Theory]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/Workspace")]
    [InlineData("asazure://westeurope.asazure.windows.net/server")]
    [InlineData("link://resource/")]
    [InlineData("localhost:52123")]
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
