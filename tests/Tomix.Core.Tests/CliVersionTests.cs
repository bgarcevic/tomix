using Tomix.Core.Update;

namespace Tomix.Core.Tests;

public sealed class CliVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData("v1.2.3", 1, 2, 3, null)]
    [InlineData("0.1.0-alpha.0.12", 0, 1, 0, "alpha.0.12")]
    [InlineData("1.2.3+abc123", 1, 2, 3, null)]
    [InlineData("1.2.3-rc.1+abc123", 1, 2, 3, "rc.1")]
    public void TryParse_AcceptsValidVersions(string input, int major, int minor, int patch, string? prerelease)
    {
        Assert.True(CliVersion.TryParse(input, out var version));
        Assert.Equal(new CliVersion(major, minor, patch, prerelease), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.x")]
    [InlineData("latest")]
    [InlineData("1.2.3-")]
    [InlineData("-1.2.3")]
    public void TryParse_RejectsInvalidVersions(string? input)
    {
        Assert.False(CliVersion.TryParse(input, out _));
    }

    [Theory]
    [InlineData("0.1.0-alpha.0.12", "0.1.0")] // MinVer dev build < its release
    [InlineData("1.0.0-alpha.2", "1.0.0-alpha.10")] // numeric prerelease ids compare numerically
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")] // fewer identifiers < more identifiers
    [InlineData("1.0.0-alpha.1", "1.0.0-beta.1")]
    [InlineData("1.0.0-1", "1.0.0-alpha")] // numeric ids sort below alphanumeric
    [InlineData("0.9.9", "0.10.0")]
    [InlineData("1.9.9", "2.0.0")]
    [InlineData("2.0.0-rc.1", "2.0.0")]
    public void CompareTo_OrdersVersions(string lower, string higher)
    {
        Assert.True(CliVersion.TryParse(lower, out var lowerVersion));
        Assert.True(CliVersion.TryParse(higher, out var higherVersion));

        Assert.True(higherVersion.IsNewerThan(lowerVersion));
        Assert.False(lowerVersion.IsNewerThan(higherVersion));
        Assert.True(lowerVersion < higherVersion);
        Assert.True(higherVersion > lowerVersion);
    }

    [Theory]
    [InlineData("1.2.3", "v1.2.3")]
    [InlineData("1.2.3", "1.2.3+build.5")]
    public void CompareTo_TreatsEquivalentVersionsAsEqual(string left, string right)
    {
        Assert.True(CliVersion.TryParse(left, out var leftVersion));
        Assert.True(CliVersion.TryParse(right, out var rightVersion));

        Assert.Equal(0, leftVersion.CompareTo(rightVersion));
        Assert.False(leftVersion.IsNewerThan(rightVersion));
        Assert.False(rightVersion.IsNewerThan(leftVersion));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3-rc.1", "1.2.3-rc.1")]
    public void ToString_RoundTrips(string input, string expected)
    {
        Assert.True(CliVersion.TryParse(input, out var version));
        Assert.Equal(expected, version.ToString());
    }
}
