using Tomix.Cli.Commands;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

public class ConnectCommandWorkspaceTests
{
    // Local primary (model is not null): bare workspace names expand to a powerbi:// endpoint,
    // mirroring the primary-server normalization. This is the regression that caused a bare
    // `-w` value to be stored verbatim and rendered as a never-reached mirror target.
    [Theory]
    [InlineData("MyWorkspace", "powerbi://api.powerbi.com/v1.0/myorg/MyWorkspace")]
    [InlineData("My Workspace", "powerbi://api.powerbi.com/v1.0/myorg/My Workspace")]
    [InlineData("test", "powerbi://api.powerbi.com/v1.0/myorg/test")]
    public void NormalizeWorkspaceTarget_LocalPrimary_ExpandsBareName(string input, string expected)
    {
        var normalized = ConnectCommand.NormalizeWorkspaceTarget(model: "./local-model", input);

        Assert.Equal(expected, normalized);
        Assert.True(ModelReference.IsRemoteEndpoint(normalized));
    }

    // Local primary: endpoints that already identify as remote or local-instance pass through.
    [Theory]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/Workspace")]
    [InlineData("asazure://westeurope.asazure.windows.net/server")]
    [InlineData("localhost:52123")]
    [InlineData("127.0.0.1:52123")]
    public void NormalizeWorkspaceTarget_LocalPrimary_PassesEndpointsThrough(string input)
        => Assert.Equal(input, ConnectCommand.NormalizeWorkspaceTarget(model: "./local-model", input));

    // Percent-escaped workspace names (e.g. pasted from a browser URL) are decoded before being
    // expanded, so the stored mirror matches the real workspace name the XMLA endpoint expects.
    [Theory]
    [InlineData("sandbox%20bkg", "powerbi://api.powerbi.com/v1.0/myorg/sandbox bkg")]
    [InlineData("My%20Workspace", "powerbi://api.powerbi.com/v1.0/myorg/My Workspace")]
    public void NormalizeWorkspaceTarget_LocalPrimary_DecodesPercentEscapes(string input, string expected)
    {
        var normalized = ConnectCommand.NormalizeWorkspaceTarget(model: "./local-model", input);

        Assert.Equal(expected, normalized);
        Assert.True(ModelReference.IsRemoteEndpoint(normalized));
    }

    // An already-formed endpoint with percent escapes is decoded too, so a pasted URL resolves.
    [Fact]
    public void NormalizeWorkspaceTarget_LocalPrimary_DecodesPercentEscapesInEndpoint()
    {
        var normalized = ConnectCommand.NormalizeWorkspaceTarget(
            model: "./local-model",
            "powerbi://api.powerbi.com/v1.0/myorg/sandbox%20bkg");

        Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/sandbox bkg", normalized);
        Assert.True(ModelReference.IsRemoteEndpoint(normalized));
    }

    // Local primary: a missing workspace stays missing.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeWorkspaceTarget_LocalPrimary_NullOrWhitespace_ReturnsAsIs(string? input)
        => Assert.Equal(input, ConnectCommand.NormalizeWorkspaceTarget(model: "./local-model", input));

    // Remote primary (model is null): -w is documented as a local folder/.bim target and must
    // NEVER be expanded to a powerbi:// URL. A bare name here is left untouched so the
    // local-folder-init branch can handle it.
    [Theory]
    [InlineData("MyWorkspace")]
    [InlineData("./mirror-folder")]
    [InlineData("C:\\models\\mirror.bim")]
    [InlineData("mirror")]
    public void NormalizeWorkspaceTarget_RemotePrimary_NeverExpands(string input)
    {
        var normalized = ConnectCommand.NormalizeWorkspaceTarget(model: null, input);

        Assert.Equal(input, normalized);
        Assert.False(ModelReference.IsRemoteEndpoint(normalized));
    }

    // Remote primary: an already-remote -w value (unusual but valid) is preserved.
    [Fact]
    public void NormalizeWorkspaceTarget_RemotePrimary_PreservesRemoteEndpoint()
        => Assert.Equal(
            "powerbi://api.powerbi.com/v1.0/myorg/Workspace",
            ConnectCommand.NormalizeWorkspaceTarget(model: null, "powerbi://api.powerbi.com/v1.0/myorg/Workspace"));

    // The remote reports the canonical dataset name; it wins over the user-typed value so the
    // stored mirror target matches exactly (Power BI blocks casing-change renames via XMLA).
    [Theory]
    [InlineData("Contoso_core", "Contoso_Core", "Contoso_Core")]
    [InlineData("Contoso_Core", "Contoso_core", "Contoso_core")]
    [InlineData("MyModel", "MyModel", "MyModel")]
    public void ResolveWorkspaceDatabase_PrefersRemoteName(string requested, string resolved, string expected)
        => Assert.Equal(expected, ConnectCommand.ResolveWorkspaceDatabase(requested, resolved));

    // When the remote didn't report a name (new dataset, or summary lacked one), keep the
    // user-typed value rather than blanking the target.
    [Theory]
    [InlineData("Contoso_core", null)]
    [InlineData("Contoso_core", "")]
    [InlineData("Contoso_core", "   ")]
    public void ResolveWorkspaceDatabase_BlankRemote_FallsBackToRequested(string requested, string? resolved)
        => Assert.Equal(requested, ConnectCommand.ResolveWorkspaceDatabase(requested, resolved));
}
