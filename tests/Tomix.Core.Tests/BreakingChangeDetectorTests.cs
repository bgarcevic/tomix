using Tomix.Core.Update;

namespace Tomix.Core.Tests;

public sealed class BreakingChangeDetectorTests
{
    [Theory]
    [InlineData("* feat!: drop legacy flags by @user in https://github.com/x/y/pull/12")]
    [InlineData("* fix(auth)!: reject argv secrets by @user in #34")]
    [InlineData("- refactor(app)!: delete ambient store construction")]
    [InlineData("polish!: align connect exit codes")]
    [InlineData("Some intro.\n\n* chore: bump deps\n* feat(cli)!: rename option")]
    [InlineData("BREAKING CHANGE: exit codes renumbered")]
    [InlineData("This release contains a breaking-change to the JSON contract.")]
    [InlineData("## Breaking changes\n\n- everything")]
    public void IsBreaking_DetectsBreakingMarkers(string body)
    {
        Assert.True(BreakingChangeDetector.IsBreaking(body));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("* feat: add tx update command")]
    [InlineData("* fix(scope): handle empty input")]
    [InlineData("All changes are non-breaking and backward compatible.")]
    [InlineData("We shout excitedly! : but this is not a conventional commit marker")]
    public void IsBreaking_IgnoresNonBreakingNotes(string? body)
    {
        Assert.False(BreakingChangeDetector.IsBreaking(body));
    }
}
