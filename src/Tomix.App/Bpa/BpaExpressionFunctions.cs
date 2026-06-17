using System.Linq.Dynamic.Core.CustomTypeProviders;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Tomix.App.Bpa;

/// <summary>
/// Regex helper exposed to rule expressions as <c>RegEx.IsMatch(field, pattern)</c>
/// (the rule dialect's spelling). Returns <c>false</c> for null input rather than throwing.
/// </summary>
public static class RegEx
{
    public static bool IsMatch(string? input, string pattern)
        => input is not null && Regex.IsMatch(input, pattern);
}

/// <summary>
/// Exposes the small set of types a BPA rule <c>Expression</c> may call into
/// (<see cref="RegEx"/>, <see cref="Convert"/>, <see cref="Math"/>, <see cref="string"/>) to the
/// dynamic expression parser. Enum-style literals (<c>DataType.Decimal</c>,
/// <c>CrossFilteringBehavior.BothDirections</c>, …) are handled by string normalization in
/// <see cref="BpaExpressionEvaluator"/>, so they need no type here.
/// </summary>
internal sealed class BpaTypeProvider : IDynamicLinqCustomTypeProvider
{
    private static readonly HashSet<Type> Types = BuildTypes();

    private static HashSet<Type> BuildTypes()
    {
        var types = new HashSet<Type>
        {
            typeof(RegEx),
            typeof(Convert),
            typeof(Math),
            typeof(string),
        };

        // Make instance methods on the adapter object model callable (e.g. GetAnnotation). Property
        // access on the iterator already works without registration; method calls require it.
        foreach (var type in typeof(Model.BpaObject).Assembly.GetTypes())
            if (type is { IsClass: true, Namespace: "Tomix.App.Bpa.Model" })
                types.Add(type);

        return types;
    }

    public HashSet<Type> GetCustomTypes() => Types;

    public Dictionary<Type, List<MethodInfo>> GetExtensionMethods() => [];

    public Type? ResolveType(string typeName)
        => Types.FirstOrDefault(t => t.FullName == typeName) ?? Type.GetType(typeName);

    public Type? ResolveTypeBySimpleName(string simpleTypeName)
        => Types.FirstOrDefault(t => t.Name == simpleTypeName);
}
