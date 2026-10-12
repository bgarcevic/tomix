using Tomix.Core.Diagnostics;

namespace Tomix.Core.Tests;

public sealed class NameSuggestionTests
{
    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("Amount", "amout", 1)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "", 3)]
    [InlineData("same", "SAME", 0)]
    public void Distance_IsCaseInsensitiveLevenshtein(string a, string b, int expected)
        => Assert.Equal(expected, NameSuggestion.Distance(a, b));

    [Theory]
    [InlineData("Amout", 3, "Amount")]
    [InlineData("Zzzzzz", 3, null)]
    [InlineData("Amout", 0, null)]
    public void Closest_HonorsTheLimit(string input, int maxDistance, string? expected)
        => Assert.Equal(expected, NameSuggestion.Closest(input, ["Quantity", "Amount", "Price"], maxDistance));

    [Fact]
    public void Closest_BreaksTiesByCandidateOrder()
        => Assert.Equal("Cat", NameSuggestion.Closest("Bat", ["Cat", "Hat"], 1));

    [Theory]
    [InlineData("ab", 2)]
    [InlineData("Total Sales", 3)]
    public void ScaledLimit_IsAThirdOfTheLengthAtLeastTwo(string input, int expected)
        => Assert.Equal(expected, NameSuggestion.ScaledLimit(input));
}
