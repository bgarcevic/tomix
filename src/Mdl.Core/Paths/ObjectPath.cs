using System.Text;
using System.Text.RegularExpressions;
using Mdl.Core.Models;

namespace Mdl.Core.Paths;

/// <summary>
/// Parses an object-path filter (e.g. <c>Sales/Measures</c>, <c>Roles/Re*/Members</c>,
/// <c>'Net Sales'/'Sales Amount'</c>) into its segments. Segments are separated by <c>/</c>;
/// single quotes group a name containing spaces or slashes and force a literal match.
/// </summary>
public static class ObjectPath
{
    /// <summary>
    /// Splits <paramref name="path"/> into segments, honouring single-quoted groups.
    /// Returns an empty list for a null/blank path.
    /// </summary>
    public static IReadOnlyList<PathSegment> Parse(string? path)
    {
        var segments = new List<PathSegment>();
        if (string.IsNullOrWhiteSpace(path))
            return segments;

        var text = new StringBuilder();
        var inQuotes = false;
        var hadQuote = false;

        void Flush()
        {
            if (text.Length > 0 || hadQuote)
                segments.Add(new PathSegment(text.ToString(), hadQuote));
            text.Clear();
            hadQuote = false;
        }

        foreach (var c in path)
        {
            switch (c)
            {
                case '\'':
                    inQuotes = !inQuotes;
                    hadQuote = true;
                    break;
                case '/' when !inQuotes:
                    Flush();
                    break;
                default:
                    text.Append(c);
                    break;
            }
        }

        Flush();
        return segments;
    }
}

/// <summary>
/// A single segment of an object path. A segment is either a container keyword (e.g. <c>Measures</c>)
/// that pivots into a collection, or a name that matches objects literally or by wildcard.
/// Quoting (<see cref="IsQuoted"/>) forces a literal name and disables keyword/wildcard meaning.
/// </summary>
public sealed record PathSegment(string Text, bool IsQuoted)
{
    private static readonly IReadOnlyDictionary<string, ModelObjectKind> Keywords =
        new Dictionary<string, ModelObjectKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["Tables"] = ModelObjectKind.Table,
            ["Measures"] = ModelObjectKind.Measure,
            ["Columns"] = ModelObjectKind.Column,
            ["Hierarchies"] = ModelObjectKind.Hierarchy,
            ["Partitions"] = ModelObjectKind.Partition,
            ["Relationships"] = ModelObjectKind.Relationship,
            ["Roles"] = ModelObjectKind.Role,
            ["Perspectives"] = ModelObjectKind.Perspective,
            ["Cultures"] = ModelObjectKind.Culture,
            ["Levels"] = ModelObjectKind.Level,
            ["Members"] = ModelObjectKind.RoleMember
        };

    /// <summary>True when the segment is an unquoted name containing <c>*</c> or <c>?</c>.</summary>
    public bool IsWildcard => !IsQuoted && (Text.Contains('*') || Text.Contains('?'));

    /// <summary>
    /// True when the segment names a container keyword (and is not quoted). Keywords take
    /// precedence over a literal object of the same name; quote the name to match it literally.
    /// </summary>
    public bool IsKeyword => TryGetKeyword(out _);

    /// <summary>True for a non-keyword, non-wildcard segment that names a single object exactly.</summary>
    public bool IsExactLiteral => !IsKeyword && !IsWildcard;

    /// <summary>Resolves the segment to a container kind when it is a keyword.</summary>
    public bool TryGetKeyword(out ModelObjectKind kind)
    {
        if (!IsQuoted && Keywords.TryGetValue(Text, out kind))
            return true;

        kind = default;
        return false;
    }

    /// <summary>Case-insensitively matches an object name against this segment (literal or glob).</summary>
    public bool NameMatch(string name) => IsWildcard
        ? GlobMatch(Text, name)
        : string.Equals(Text, name, StringComparison.OrdinalIgnoreCase);

    private static bool GlobMatch(string pattern, string value)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
