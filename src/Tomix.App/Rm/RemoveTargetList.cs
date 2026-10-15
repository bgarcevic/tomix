using System.Text.Json;
using Tomix.Core.Models;

namespace Tomix.App.Rm;

/// <summary>One object a batch <c>rm</c> was asked to remove. <paramref name="Input"/> is how the
/// caller spelled it (a path argument or a <c>--from</c> line), echoed back in the result.</summary>
public sealed record RemoveTarget(string Path, ModelObjectKind? Type = null, string? Input = null)
{
    public string Label => Input ?? Path;
}

/// <summary>
/// Parses the <c>rm --from</c> input: one target per line, either a tomix object path or a JSON
/// object (JSON Lines). A JSON line names its object as <c>{"type", "table"?, "name"}</c>
/// (levels add <c>hierarchy</c>, role members and table permissions add <c>role</c>) or as
/// <c>{"type"?, "path"}</c>. Unknown fields are ignored, so producers can add their own; blank
/// lines and lines starting with <c>#</c> are skipped.
/// </summary>
public static class RemoveTargetList
{
    public sealed record ParseResult(IReadOnlyList<RemoveTarget> Targets, IReadOnlyList<string> Errors);

    public static ParseResult Parse(string text)
    {
        var targets = new List<RemoveTarget>();
        var errors = new List<string>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (!line.StartsWith('{'))
            {
                targets.Add(new RemoveTarget(line, Input: line));
                continue;
            }

            if (TryParseJson(line, out var target, out var error))
                targets.Add(target!);
            else
                errors.Add($"line {i + 1}: {error}");
        }

        return new ParseResult(targets, errors);
    }

    /// <summary>Parses an object-kind name leniently: the <c>--type</c> vocabulary plus the
    /// spellings other tools use (<c>Calculated Table</c>, <c>calculation_group</c>,
    /// <c>NamedExpression</c>, ...). Calculated tables and groups are tables; calculated
    /// columns match as columns.</summary>
    public static bool TryParseKind(string? value, out ModelObjectKind kind)
    {
        var normalized = new string((value ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        switch (normalized)
        {
            case "calculatedtable" or "calctable" or "calculationgroup" or "calcgroup":
                kind = ModelObjectKind.Table;
                return true;
            case "calculatedcolumn" or "calccolumn" or "datacolumn":
                kind = ModelObjectKind.Column;
                return true;
            case "namedexpression" or "sharedexpression" or "parameter":
                kind = ModelObjectKind.Expression;
                return true;
            case "userdefinedfunction" or "udf":
                kind = ModelObjectKind.Function;
                return true;
            default:
                return ModelObjectKindParser.TryParse(normalized, out kind);
        }
    }

    private static bool TryParseJson(string line, out RemoveTarget? target, out string? error)
    {
        target = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            error = $"invalid JSON ({ex.Message})";
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "expected a JSON object";
                return false;
            }

            var root = document.RootElement;
            var typeText = Field(root, "type");
            ModelObjectKind? kind = null;
            if (typeText is not null)
            {
                if (!TryParseKind(typeText, out var parsed))
                {
                    error = $"unknown type '{typeText}'. Expected one of: {ModelObjectTypeCatalog.DiscoveryListText}.";
                    return false;
                }

                kind = parsed;
            }

            if (Field(root, "path") is { } path)
            {
                target = new RemoveTarget(path, kind, line);
                error = null;
                return true;
            }

            if (kind is null)
            {
                error = "expected \"path\", or \"type\" and \"name\"";
                return false;
            }

            error = BuildPath(kind.Value, root, out var built);
            if (error is not null)
                return false;

            target = new RemoveTarget(built!, kind, line);
            return true;
        }
    }

    /// <summary>The object path for a <c>{type, table?, name}</c> line, as the snapshot spells it.</summary>
    private static string? BuildPath(ModelObjectKind kind, JsonElement root, out string? path)
    {
        path = null;
        var name = Field(root, "name");
        var table = Field(root, "table");
        var role = Field(root, "role");
        if (kind == ModelObjectKind.Kpi)
            name ??= Field(root, "measure");
        if (kind == ModelObjectKind.TablePermission)
            name ??= table;
        if (kind == ModelObjectKind.RefreshPolicy)
            name = "RefreshPolicy";

        if (string.IsNullOrWhiteSpace(name))
            return "missing \"name\"";

        switch (kind)
        {
            case ModelObjectKind.Table:
                path = Segment(name);
                return null;
            case ModelObjectKind.Measure or ModelObjectKind.Column or ModelObjectKind.CalculatedColumn
                or ModelObjectKind.Hierarchy or ModelObjectKind.Partition or ModelObjectKind.CalculationItem
                or ModelObjectKind.Calendar or ModelObjectKind.Kpi or ModelObjectKind.RefreshPolicy:
                if (string.IsNullOrWhiteSpace(table))
                    return $"missing \"table\" for type '{Field(root, "type")}'";
                path = kind == ModelObjectKind.Kpi
                    ? $"{Segment(table)}/{Segment(name)}/KPI"
                    : kind == ModelObjectKind.RefreshPolicy
                        ? $"{Segment(table)}/RefreshPolicy"
                        : $"{Segment(table)}/{Segment(name)}";
                return null;
            case ModelObjectKind.Level:
                if (string.IsNullOrWhiteSpace(table) || Field(root, "hierarchy") is not { } hierarchy)
                    return "missing \"table\" or \"hierarchy\" for type 'level'";
                path = $"{Segment(table)}/{Segment(hierarchy)}/{Segment(name)}";
                return null;
            case ModelObjectKind.RoleMember or ModelObjectKind.TablePermission:
                if (string.IsNullOrWhiteSpace(role))
                    return $"missing \"role\" for type '{Field(root, "type")}'";
                path = $"Roles/{Segment(role)}/{Segment(name)}";
                return null;
            default:
                path = $"{Container(kind)}/{Segment(name)}";
                return null;
        }
    }

    private static string Container(ModelObjectKind kind) => kind switch
    {
        ModelObjectKind.Relationship => "Relationships",
        ModelObjectKind.Role => "Roles",
        ModelObjectKind.Perspective => "Perspectives",
        ModelObjectKind.Culture => "Cultures",
        ModelObjectKind.DataSource => "DataSources",
        ModelObjectKind.Expression => "Expressions",
        ModelObjectKind.Function => "Functions",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    /// <summary>A name as one path segment: quoted when it contains a slash, like snapshot paths.</summary>
    private static string Segment(string name)
        => name.Contains('/') ? $"'{name.Replace("'", "''", StringComparison.Ordinal)}'" : name;

    /// <summary>A string field, matched case-insensitively; other value kinds read as absent.</summary>
    private static string? Field(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        }

        return null;
    }
}
