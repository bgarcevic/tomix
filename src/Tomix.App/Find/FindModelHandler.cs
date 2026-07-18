using System.Text.RegularExpressions;
using Tomix.App.Diagnostics;
using Tomix.App.ModelObjects;
using Tomix.Core.Models;
using Tomix.Core.Properties;
using Tomix.Core.Results;

namespace Tomix.App.Find;

public sealed class FindModelHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public FindModelHandler(IEnumerable<IModelProvider> providers)
        => _providers = providers.ToList();

    public async Task<TomixResult<FindModelResult>> HandleAsync(
        FindModelRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Regex)
        {
            try
            {
                _ = new Regex(request.Pattern);
            }
            catch (ArgumentException ex)
            {
                return TomixResult<FindModelResult>.Fail(
                    code: "TOMIX_FIND_INVALID_REGEX",
                    message: ex.Message,
                    exitCode: 2,
                    hint: "Fix the regex pattern, or remove --regex to search for literal text.");
            }
        }

        var provider = _providers.ResolveSingle(request.Model);

        if (provider is null)
            return TomixResult<FindModelResult>.Fail(
                code: "TOMIX_NO_PROVIDER",
                message: $"No provider can open model: {request.Model.Value}",
                exitCode: 2,
                hint: "Supported formats: TMDL folder, .bim file. For remote models, use --server and --database.");

        return await ProviderConnectionGuard.RunAsync(request.Model, async () =>
        {
            await using var session = await provider.OpenAsync(request.Model, cancellationToken);
            var snapshot = await session.GetSnapshotAsync(cancellationToken);

            var matches = new List<FindMatch>();
            foreach (var obj in ModelObjectProjection.Flatten(snapshot).Where(IsSearchableByDefault))
            {
                foreach (var (field, value) in SearchFields(obj, request.Scope))
                {
                    if (value is null || !TryMatch(
                            value,
                            request.Pattern,
                            request.Regex,
                            request.CaseSensitive,
                            out var matchedText,
                            out var line,
                            out var position))
                        continue;

                    matches.Add(new FindMatch(
                        obj.Path,
                        ModelObjectProjection.KindLabel(obj.Kind),
                        obj.Name,
                        field,
                        matchedText,
                        value,
                        line,
                        position));
                }
            }

            return TomixResult<FindModelResult>.Ok(new FindModelResult(request.Pattern, matches));
        });
    }

    private static bool IsSearchableByDefault(ModelObject obj)
        => obj.Kind is not ModelObjectKind.Partition and not ModelObjectKind.Relationship;

    private static IEnumerable<(string Field, string? Value)> SearchFields(ModelObject obj, string scope)
    {
        var normalized = string.IsNullOrWhiteSpace(scope) ? "all" : scope.Trim().ToLowerInvariant();

        foreach (var descriptor in ModelPropertyCatalog.For(obj.Kind))
        {
            if (descriptor is not { Searchable: true, SearchScope: { } descriptorScope })
                continue;
            if (normalized is not "all" && normalized != descriptorScope)
                continue;

            yield return (descriptor.Header, NonEmpty(descriptor.Value(obj) as string));
        }

        // Annotations are searched only when explicitly requested: models routinely carry
        // hundreds of machine-generated annotations (PBI_*, TabularEditor_*) that would
        // drown 'all' searches in noise.
        if (normalized is "annotations" && obj.Properties is not null)
        {
            foreach (var (key, value) in obj.Properties)
            {
                if (key.StartsWith(PropertyBagKeys.AnnotationPrefix, StringComparison.Ordinal))
                    yield return (key, value);
            }
        }
    }

    private static string? NonEmpty(string? value)
        => string.IsNullOrEmpty(value) ? null : value;

    private static bool TryMatch(
        string value,
        string pattern,
        bool regex,
        bool caseSensitive,
        out string matchedText,
        out int line,
        out int position)
    {
        if (regex)
        {
            var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            var match = Regex.Match(value, pattern, options);
            if (!match.Success)
            {
                matchedText = "";
                line = 0;
                position = 0;
                return false;
            }

            matchedText = match.Value;
            (line, position) = LinePosition(value, match.Index);
            return true;
        }

        var comparison = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        var index = value.IndexOf(pattern, comparison);
        if (index < 0)
        {
            matchedText = "";
            line = 0;
            position = 0;
            return false;
        }

        matchedText = value.Substring(index, pattern.Length);
        (line, position) = LinePosition(value, index);
        return true;
    }

    private static (int Line, int Position) LinePosition(string value, int index)
    {
        var line = 1;
        var lineStart = 0;
        for (var i = 0; i < index; i++)
        {
            if (value[i] != '\n')
                continue;

            line++;
            lineStart = i + 1;
        }

        return (line, index - lineStart + 1);
    }
}
