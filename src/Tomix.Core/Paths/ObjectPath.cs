using System.Text;
using System.Text.RegularExpressions;
using Tomix.Core.Models;

namespace Tomix.Core.Paths;

/// <summary>
/// Parses an object-path filter (e.g. <c>Sales/Measures</c>, <c>Roles/Re*/Members</c>,
/// <c>'Net Sales'/'Sales Amount'</c>) into its segments. Segments are separated by <c>/</c>.
/// A single quote opens a quoted group only at the start of a segment; the group runs to the
/// next single quote and forces a literal match (names with slashes or keyword names). Inside
/// a quoted group, a doubled quote (<c>''</c>) is a literal apostrophe. An apostrophe anywhere
/// else is an ordinary character, so <c>KPI'er</c> needs no quoting at all.
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
        var atSegmentStart = true;

        void Flush()
        {
            if (text.Length > 0 || hadQuote)
                segments.Add(new PathSegment(text.ToString(), hadQuote));
            text.Clear();
            hadQuote = false;
            atSegmentStart = true;
        }

        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];

            if (inQuotes)
            {
                if (c != '\'')
                {
                    text.Append(c);
                }
                else if (i + 1 < path.Length && path[i + 1] == '\'')
                {
                    text.Append('\'');
                    i++;
                }
                else
                {
                    inQuotes = false;
                }

                continue;
            }

            switch (c)
            {
                case '\'' when atSegmentStart:
                    inQuotes = true;
                    hadQuote = true;
                    atSegmentStart = false;
                    break;
                case '/':
                    Flush();
                    break;
                default:
                    text.Append(c);
                    atSegmentStart = false;
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
