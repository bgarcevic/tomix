using Tomix.App.Bpa;

namespace Tomix.App.Tests;

public sealed class BpaExpressionEvaluatorTests
{
    private sealed class Item
    {
        public string Name { get; init; } = "";
        public bool IsHidden { get; init; }
        public IReadOnlyList<Item> Children { get; init; } = [];
    }

    private static IReadOnlyList<Item> Match(string expression, params Item[] items)
    {
        var outcome = new BpaExpressionEvaluator().Evaluate(expression, items);
        Assert.Equal(BpaEvaluationStatus.Ok, outcome.Status);
        return outcome.Matches;
    }

    [Fact]
    public void Current_BareMembersStillBind()
    {
        // With the `current` keyword present, bare members must still resolve to the element.
        var hits = Match("not IsHidden and Name == current.Name",
            new Item { Name = "A", IsHidden = false },
            new Item { Name = "B", IsHidden = true });

        var hit = Assert.Single(hits);
        Assert.Equal("A", hit.Name);
    }

    [Fact]
    public void OuterIt_RefersToOuterElementInsideNestedAny()
    {
        // outerIt inside .Any(...) refers to the outer element while bare members rebind to the inner.
        var parent = new Item
        {
            Name = "X",
            Children = [new Item { Name = "X" }, new Item { Name = "other" }]
        };
        var noMatch = new Item { Name = "Y", Children = [new Item { Name = "z" }] };

        var hits = Match("Children.Any(Name == outerit.Name)", parent, noMatch);

        Assert.Equal("X", Assert.Single(hits).Name);
    }

    [Fact]
    public void StringLiteral_RegexEscapesSurvive()
    {
        // The \s in the string literal must reach the regex verbatim rather than failing to parse.
        var hits = Match("RegEx.IsMatch(Name, \"a\\s+b\")",
            new Item { Name = "a   b" },
            new Item { Name = "axb" });

        Assert.Equal("a   b", Assert.Single(hits).Name);
    }

    [Fact]
    public void UnknownMember_ReportsCompilationError()
    {
        var outcome = new BpaExpressionEvaluator().Evaluate(
            "ThisMemberDoesNotExist == 1", [new Item { Name = "A" }]);

        Assert.Equal(BpaEvaluationStatus.CompilationError, outcome.Status);
    }
}
