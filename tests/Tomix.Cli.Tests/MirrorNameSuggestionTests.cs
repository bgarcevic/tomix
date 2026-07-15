using Tomix.Cli.Commands;

namespace Tomix.Cli.Tests;

public class MirrorNameSuggestionTests
{
    // The canonical case: model + user short name yield <model>-dev-<user> so parallel devs
    // mirroring the same model into one workspace don't collide.
    [Theory]
    [InlineData("Sales", "bokg@contoso.com", "sales-dev-bokg")]
    [InlineData("contoso-edw", "bokg@contoso.com", "contoso-edw-dev-bokg")]
    [InlineData("My Model", "jane.doe@contoso.com", "my-model-dev-jane-doe")]
    public void Suggests_ModelDevUser(string model, string user, string expected)
        => Assert.Equal(expected, ConnectPrompts.SuggestMirrorDatabaseName(model, user));

    // No cached username: drop the user segment rather than trailing a dangling dash.
    [Theory]
    [InlineData("Sales", null, "sales-dev")]
    [InlineData("Sales", "", "sales-dev")]
    [InlineData("Sales", "   ", "sales-dev")]
    public void Suggests_NoUser_DropsSegment(string model, string? user, string expected)
        => Assert.Equal(expected, ConnectPrompts.SuggestMirrorDatabaseName(model, user));

    // Missing/blank model falls back to a stable placeholder.
    [Theory]
    [InlineData(null, "bokg@contoso.com", "model-dev-bokg")]
    [InlineData("", "bokg@contoso.com", "model-dev-bokg")]
    public void Suggests_NoModel_FallsBack(string? model, string user, string expected)
        => Assert.Equal(expected, ConnectPrompts.SuggestMirrorDatabaseName(model, user));

    // A username without a domain is used as-is (already a short name).
    [Fact]
    public void Suggests_UsernameWithoutDomain_UsedVerbatim()
        => Assert.Equal("sales-dev-bokg", ConnectPrompts.SuggestMirrorDatabaseName("Sales", "bokg"));
}
