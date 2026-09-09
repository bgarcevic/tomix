namespace Tomix.Core.Models;

public static class ModelObjectKindExtensions
{
    /// <summary>
    /// True when an object of kind <paramref name="actual"/> satisfies a filter for
    /// <paramref name="requested"/>. Filtering by <see cref="ModelObjectKind.Column"/> matches
    /// calculated columns too, so <c>ls --type column</c>, the <c>Columns</c> path keyword, and
    /// <c>--type column</c> disambiguation keep seeing every column; only the explicit
    /// <c>calculatedcolumn</c> filter narrows to calculated ones.
    /// </summary>
    public static bool Matches(this ModelObjectKind actual, ModelObjectKind requested)
        => actual == requested
           || (requested == ModelObjectKind.Column && actual == ModelObjectKind.CalculatedColumn);
}
