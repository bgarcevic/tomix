namespace Tomix.Core.Models;

/// <summary>
/// Parses a <c>--type</c> value into a <see cref="ModelObjectKind"/>. The accepted vocabulary
/// lives in <see cref="ModelObjectTypeCatalog"/>; this wrapper keeps the historical entry point
/// for commands that disambiguate or filter by kind. Note that <c>calculatedcolumn</c> is its
/// own kind: filtering by <c>column</c> still matches calculated columns via
/// <see cref="ModelObjectKindExtensions.Matches"/>.
/// </summary>
public static class ModelObjectKindParser
{
    public static bool TryParse(string value, out ModelObjectKind kind)
        => ModelObjectTypeCatalog.TryParse(value, out kind);
}
