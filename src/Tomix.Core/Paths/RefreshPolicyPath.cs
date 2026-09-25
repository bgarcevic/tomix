using Tomix.Core.Models;

namespace Tomix.Core.Paths;

/// <summary>Recognizes the singleton policy child, preserving quoted table names.</summary>
public static class RefreshPolicyPath
{
    /// <summary>Returns the owning table for a policy path, or null for other objects.</summary>
    public static string? Table(string path, ModelObjectKind? type = null)
    {
        if (type is not null and not ModelObjectKind.RefreshPolicy)
            return null;
        var parts = ObjectPath.Parse(path.Trim().Trim('/'));
        if (parts.Count == 3 && !parts[0].IsQuoted &&
            string.Equals(parts[0].Text, "Tables", StringComparison.OrdinalIgnoreCase))
            parts = parts.Skip(1).ToArray();
        return parts.Count == 2 &&
            string.Equals(parts[1].Text, "RefreshPolicy", StringComparison.OrdinalIgnoreCase)
            ? parts[0].Text : null;
    }
}
