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
}
