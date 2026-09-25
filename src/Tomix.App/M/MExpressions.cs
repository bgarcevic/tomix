using Tomix.App.Dax;
using Tomix.Core.Models;

namespace Tomix.App.M;

/// <summary>
/// Which model text is Power Query (M), for renderers that highlight it. The counterpart of
/// <see cref="DaxExpressions.IsDaxExpression"/>: shared expressions carry M, and so do partitions,
/// except calculated-table partitions, which carry DAX. Providers only surface a partition's
/// expression for M and calculated sources, so every other partition kind has none.
/// </summary>
public static class MExpressions
{
    /// <summary>Whether an object's main <c>Expression</c> text is M.</summary>
    public static bool IsMExpression(ModelObjectKind kind, string? detail)
        => kind is ModelObjectKind.Expression or ModelObjectKind.Partition &&
           !DaxExpressions.IsDaxExpression(kind, detail);

    /// <summary>Whether <paramref name="text"/> is <paramref name="obj"/>'s M expression.</summary>
    public static bool IsMValue(ModelObject obj, string text)
        => IsMExpression(obj.Kind, obj.Detail) &&
           string.Equals(obj.Expression, text, StringComparison.Ordinal);
}
