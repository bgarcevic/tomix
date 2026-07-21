using System.Text.RegularExpressions;
using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Paths;

namespace Tomix.Provider.Tom;

/// <summary>
/// Path and name helpers shared by the mutation collaborators: type normalization,
/// keyword-based type inference, DAX/slash path parsing, and the mutation-path regexes.
/// </summary>
internal static partial class TomMutationPaths
{
    internal static Table? FindTable(Model model, string name)
        => model.Tables.FirstOrDefault(t => NameEquals(t.Name, name));

    internal static bool NameEquals(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    internal static string Segment(string name)
        => name.Contains('/') ? $"'{name}'" : name;

    internal static string NormalizeType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return "";

        return type.Trim().ToLowerInvariant() switch
        {
            "calcmeasure" or "calculatedmeasure" => "measure",
            "calculatedtable" => "calctable",
            "calculatedcolumn" => "calccolumn",
            "calculationgroup" or "calculatedgroup" => "calcgroup",
            "calculationitem" or "calculateditem" => "calcitem",
            var normalized => normalized
        };
    }

    /// <summary>
    /// Resolves the effective type and name-only path. When the path uses container keywords
    /// (e.g. <c>tables/Sales/measures/Revenue</c>), keyword segments are stripped and the type is
    /// inferred from the deepest keyword unless <paramref name="type"/> was given explicitly.
    /// Paths without keywords are returned unchanged so existing <c>-t</c>-based usage is unaffected.
    /// </summary>
    internal static (string? Type, string Path) ResolveTypeAndPath(string? type, string path)
    {
        var segments = ObjectPath.Parse(path);
        if (segments.Count == 0 || segments.All(s => !s.IsKeyword && !IsSupplementalKeyword(s)))
            return (type, path);

        string? lastKeywordType = null;
        var nameSegments = new List<string>();
        foreach (var segment in segments)
        {
            if (segment.TryGetKeyword(out var kind))
            {
                if (TryMapKeywordToTypeName(kind) is { } mapped)
                    lastKeywordType = mapped;
            }
            else if (!segment.IsQuoted && SupplementalKeywords.TryGetValue(segment.Text, out var supplemental))
            {
                lastKeywordType = supplemental;
            }
            else
            {
                nameSegments.Add(segment.Text);
            }
        }

        if (nameSegments.Count == 0 || lastKeywordType is null)
            return (type, path);

        var effectiveType = !string.IsNullOrWhiteSpace(type) ? type : lastKeywordType;
        return (effectiveType, string.Join("/", nameSegments));
    }

    private static string? TryMapKeywordToTypeName(ModelObjectKind kind) => kind switch
    {
        ModelObjectKind.Table => "table",
        ModelObjectKind.Measure => "measure",
        ModelObjectKind.Column => "calccolumn",
        ModelObjectKind.Hierarchy => "hierarchy",
        ModelObjectKind.Level => "level",
        ModelObjectKind.Partition => "partition",
        ModelObjectKind.Role => "role",
        ModelObjectKind.RoleMember => "member",
        ModelObjectKind.Perspective => "perspective",
        ModelObjectKind.Culture => "culture",
        // ModelObjectKind.DataSource is deliberately unmapped: 'datasources/<Name>' cannot decide
        // between ProviderDataSource and StructuredDataSource, so an explicit -t is required.
        _ => null
    };

    /// <summary>
    /// Container keywords recognized only for add-path type inference. These live here instead of
    /// <see cref="PathSegment"/> so ls/find path semantics (where a table may be literally named
    /// e.g. 'Expressions') are unaffected. Quoting a segment disables the keyword meaning.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> SupplementalKeywords =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CalculationGroups"] = "calcgroup",
            ["CalcGroups"] = "calcgroup",
            ["CalculationItems"] = "calcitem",
            ["CalcItems"] = "calcitem",
            ["Expressions"] = "expression",
            ["Functions"] = "function",
            ["Calendars"] = "calendar",
            ["KPIs"] = "kpi",
            ["KPI"] = "kpi",
            ["TablePermissions"] = "tablepermission"
        };

    private static bool IsSupplementalKeyword(PathSegment segment)
        => !segment.IsQuoted && SupplementalKeywords.ContainsKey(segment.Text);

    internal static string NormalizeProperty(string property)
        => property.Trim().Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();

    internal static string NormalizePath(string path)
    {
        var trimmed = path.Trim().Trim('/');
        var dax = DaxObjectPath().Match(trimmed);
        if (dax.Success)
        {
            var table = dax.Groups["qtable"].Success
                ? dax.Groups["qtable"].Value.Replace("''", "'", StringComparison.Ordinal)
                : dax.Groups["table"].Value;
            return $"{table}/{dax.Groups["object"].Value}";
        }

        return trimmed.Replace("'", "", StringComparison.Ordinal);
    }

    internal static IReadOnlyList<string> SplitObjectPath(string path)
        => NormalizePath(path)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    internal static string RelationshipDisplay(SingleColumnRelationship relationship)
        => $"{relationship.FromColumn.Table.Name}[{relationship.FromColumn.Name}] -> " +
           $"{relationship.ToColumn.Table.Name}[{relationship.ToColumn.Name}]";

    // Quoted table names may contain escaped apostrophes ('Høreprøver KPI''er'); unquoted ones
    // may not contain quotes or brackets. The qtable group is unescaped by ParseMutationPath.
    [GeneratedRegex("^(?:'(?<qtable>(?:[^']|'')+)'|(?<table>[^'\\[\\]]+))\\[(?<object>[^\\]]+)\\]$")]
    internal static partial Regex DaxObjectPath();

    // Accepts 'Table'[Column]->'Table'[Column] and Table[Column]->Table[Column] (whitespace tolerated).
    [GeneratedRegex(@"^\s*(?:'(?<ft>[^']+)'|(?<ft>[^'\[\]]+?))\s*\[(?<fc>[^\]]+)\]\s*->\s*(?:'(?<tt>[^']+)'|(?<tt>[^'\[\]]+?))\s*\[(?<tc>[^\]]+)\]\s*$")]
    internal static partial Regex RelationshipPath();
}
