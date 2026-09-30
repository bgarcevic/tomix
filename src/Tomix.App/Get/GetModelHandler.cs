using Tomix.App.Dax;
using Tomix.App.Deps;
using Tomix.App.Get;
using Tomix.App.ModelObjects;
using Tomix.App.Models;
using Tomix.Core.Models;
using Tomix.Core.Paths;
using Tomix.Core.Properties;
using Tomix.Core.Results;

namespace Tomix.App.Get;

/// <summary>
/// The one read pipeline: resolve the path against the snapshot, then read one object, list the
/// matches, trace dependencies, or find unused objects. <c>get</c>, <c>ls</c> and <c>deps</c> all
/// run through here, so path grammar, lookup errors and projections cannot drift between them.
/// </summary>
public sealed class GetModelHandler
{
    private const string NotFoundHint = "Run 'tx get' to list the tables, or 'tx get \"Sa*\"' to filter.";

    private readonly IReadOnlyList<IModelProvider> _providers;

    public GetModelHandler(IEnumerable<IModelProvider> providers)
        => _providers = providers.ToList();

    /// <summary>
    /// The mode a request runs in once <see cref="GetMode.Auto"/> is settled. Pure, so the CLI
    /// can validate flags and output formats before the model is opened.
    /// </summary>
    public static GetMode ResolveMode(GetModelRequest request)
    {
        if (request.Mode != GetMode.Auto)
            return request.Mode;
        if (request.Where is { Count: > 0 })
            return GetMode.List;
        return IsSelection(request.Path) ? GetMode.List : GetMode.Object;
    }

    /// <summary>
    /// True when the path selects a set rather than naming one object: no path (the tables), a
    /// wildcard in any segment, or a path ending in a container keyword (<c>Sales/Measures</c>).
    /// </summary>
    public static bool IsSelection(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;

        var trimmed = path.Trim();
        // The model root and DAX forms ('Sales'[Amount], [Total Sales]) always name one object.
        if (trimmed.Trim('/') == "." || trimmed.Contains('['))
            return false;

        var segments = ObjectPath.Parse(trimmed);
        return segments.Count == 0
               || segments.Any(segment => segment.IsWildcard)
               || segments[^1].IsKeyword;
    }

    public async Task<TomixResult<GetModelResult>> HandleAsync(
        GetModelRequest request,
        CancellationToken cancellationToken)
    {
        var mode = ResolveMode(request);

        if (Validate(request, mode) is { } invalid)
            return invalid;

        return await ModelSessionRunner.RunAsync(_providers, request.Model, async session =>
        {
            var snapshot = await session.GetSnapshotAsync(cancellationToken);

            return mode switch
            {
                GetMode.List => List(snapshot, request),
                GetMode.Unused => Ok(mode, deps: new GetDepsResult(
                    Path: "",
                    Type: "",
                    Upstream: [],
                    Downstream: [],
                    Unused: DependencyGraph.FromSnapshot(snapshot).Unused(request.HiddenOnly))),
                GetMode.Deps => Single(snapshot, request.Path!, request.Type, target => Deps(snapshot, target, request)),
                _ => Read(snapshot, request)
            };
        }, cancellationToken);
    }

    private static TomixResult<GetModelResult>? Validate(GetModelRequest request, GetMode mode)
    {
        var hasPath = !string.IsNullOrWhiteSpace(request.Path);

        if (mode == GetMode.Unused && hasPath)
            return Usage(
                "TOMIX_UNUSED_PATH",
                "--unused scans the whole model and takes no path.",
                "Drop the path, or trace one object with --deps.");

        if (mode == GetMode.Deps && !hasPath)
            return Usage(
                "TOMIX_DEPS_PATH_REQUIRED",
                "A dependency path is required unless --unused is specified.",
                "Name one object: tx get Sales/Amount --deps.");

        if (mode == GetMode.List && !string.IsNullOrWhiteSpace(request.Query))
            return SingleObjectRequired(request.Path, "--query");

        if (mode == GetMode.Deps && IsSelection(request.Path))
            return SingleObjectRequired(request.Path, "--deps");

        return null;
    }

    private static TomixResult<GetModelResult> SingleObjectRequired(string? path, string option)
        => Usage(
            "TOMIX_SINGLE_OBJECT_REQUIRED",
            string.IsNullOrWhiteSpace(path)
                ? $"{option} reads one object; name it with a path."
                : $"{option} reads one object, but '{path}' selects a set.",
            "Name a single object, for example Sales/Amount, or drop the option to list the matches.");

    private static TomixResult<GetModelResult> Usage(string code, string message, string hint)
        => TomixResult<GetModelResult>.Fail(code: code, message: message, exitCode: 2, hint: hint);

    private static TomixResult<GetModelResult> Ok(
        GetMode mode,
        GetObjectResult? obj = null,
        GetListResult? list = null,
        GetDepsResult? deps = null)
        => TomixResult<GetModelResult>.Ok(new GetModelResult(mode, obj, list, deps));

    private static TomixResult<GetModelResult> Read(ModelSnapshot snapshot, GetModelRequest request)
    {
        var measureNames = DaxModelNames.MeasureNames(snapshot);

        // The model root is not a snapshot object: "." synthesizes one from the
        // snapshot's model-level properties so get can read back what set accepts
        // on the root (culture, compatibility level, and friends).
        if (request.Path!.Trim().Trim('/') == ".")
            return Project(ModelRoot(snapshot), request.Query, measureNames);

        return Single(snapshot, request.Path, request.Type, obj => Project(obj, request.Query, measureNames));
    }

    /// <summary>Resolves a path that must name exactly one object, then hands it on.</summary>
    private static TomixResult<GetModelResult> Single(
        ModelSnapshot snapshot,
        string path,
        ModelObjectKind? type,
        Func<ModelObject, TomixResult<GetModelResult>> next)
    {
        var matches = ModelObjectLookup.Find(snapshot, path, type).ToList();

        if (matches.Count == 0)
            return TomixResult<GetModelResult>.Fail(
                code: "TOMIX_OBJECT_NOT_FOUND",
                message: ModelObjectLookup.NotFoundMessage(path),
                exitCode: 1,
                hint: NotFoundHint);

        if (matches.Count > 1)
            return TomixResult<GetModelResult>.Fail(
                code: "TOMIX_OBJECT_AMBIGUOUS",
                message: AmbiguousMatchMessage.For(path, matches),
                exitCode: 1,
                hint: AmbiguousMatchMessage.Hint);

        return next(matches[0]);
    }

    private static TomixResult<GetModelResult> List(ModelSnapshot snapshot, GetModelRequest request)
    {
        var matches = ModelObjectSelector
            .Select(snapshot, request.Path, request.Type)
            .Select(o => (Object: o, Projected: ModelPropertyCatalog.Project(o)))
            .ToList();

        foreach (var filter in request.Where ?? [])
        {
            if (matches.Count > 0 && !matches.Any(m => filter.IsKnownIn(m.Projected)))
                return TomixResult<GetModelResult>.Fail(
                    code: "TOMIX_PROPERTY_NOT_FOUND",
                    message: $"No object in scope has a property '{filter.Property}'.",
                    exitCode: 1,
                    hint: PropertyHint(filter.Property, matches
                        .SelectMany(m => m.Projected.Keys)
                        .Where(k => !IsBagToken(k))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()));

            matches = matches.Where(m => filter.Matches(m.Projected)).ToList();
        }

        var objects = matches
            .Select(m => new GetListObject(
                m.Object.Path, m.Object.Name, m.Object.Kind, m.Object.Detail, m.Object.Expression,
                m.Object.Description, m.Object.Hidden, m.Object.SourceColumn,
                m.Object.Children.GroupBy(c => c.Kind).ToDictionary(g => g.Key, g => g.Count()),
                m.Projected))
            .ToList();

        return Ok(GetMode.List, list: new GetListResult(
            snapshot.Name, snapshot.CompatibilityLevel, objects, DaxModelNames.MeasureNames(snapshot)));
    }

    private static TomixResult<GetModelResult> Deps(ModelSnapshot snapshot, ModelObject target, GetModelRequest request)
    {
        var graph = DependencyGraph.FromSnapshot(snapshot);
        var maxDepth = request.MaxDepth > 0 ? request.MaxDepth : int.MaxValue;

        IReadOnlyList<DependencyObject> upstream = request.Direction == DepsDirection.Downstream
            ? []
            : request.Deep ? graph.Deep(target, upstream: true, maxDepth) : graph.DirectUpstream(target);
        IReadOnlyList<DependencyObject> downstream = request.Direction == DepsDirection.Upstream
            ? []
            : request.Deep ? graph.Deep(target, upstream: false, maxDepth) : graph.DirectDownstream(target);

        return Ok(GetMode.Deps, deps: new GetDepsResult(
            target.Path,
            ModelObjectProjection.KindLabel(target.Kind),
            upstream,
            downstream));
    }

    private static ModelObject ModelRoot(ModelSnapshot snapshot)
        => new(
            snapshot.Name,
            ModelObjectKind.Model,
            ".",
            Detail: null,
            Expression: null,
            Description: snapshot.Description,
            Hidden: false,
            SourceColumn: null,
            Children: [],
            Properties: snapshot.Properties);

    private static TomixResult<GetModelResult> Project(ModelObject obj, string? query, IReadOnlySet<string> measureNames)
    {
        var properties = ModelPropertyCatalog.Project(obj);
        var kind = ModelObjectProjection.KindLabel(obj.Kind);

        if (!string.IsNullOrWhiteSpace(query))
        {
            var match = properties.FirstOrDefault(p =>
                string.Equals(p.Key, query, StringComparison.OrdinalIgnoreCase));
            if (match.Key is not null)
                properties = new Dictionary<string, object?> { [match.Key] = match.Value };
            // An annotation or translation that was never set reads back as null: the token is
            // well-formed, the object just carries no value for it.
            else if (IsBagToken(query))
                properties = new Dictionary<string, object?> { [query] = null };
            else
                return TomixResult<GetModelResult>.Fail(
                    code: "TOMIX_PROPERTY_NOT_FOUND",
                    message: $"{kind} '{obj.Path}' has no property '{query}'.",
                    exitCode: 1,
                    hint: PropertyHint(query, properties.Keys.Where(k => !IsBagToken(k)).ToList()));
        }

        return Ok(GetMode.Object, obj: new GetObjectResult(
            kind,
            obj.Path,
            properties,
            obj,
            measureNames));
    }

    private static bool IsBagToken(string key)
        => key.StartsWith("annotation:", StringComparison.OrdinalIgnoreCase)
           || key.StartsWith("translation:", StringComparison.OrdinalIgnoreCase);

    private static string PropertyHint(string query, IReadOnlyList<string> keys)
    {
        var closest = keys
            .Select(key => (Key: key, Distance: EditDistance(query.ToLowerInvariant(), key.ToLowerInvariant())))
            .Where(candidate => candidate.Distance <= Math.Max(2, query.Length / 3))
            .OrderBy(candidate => candidate.Distance)
            .Select(candidate => candidate.Key)
            .FirstOrDefault();
        return closest is null
            ? $"Properties: {string.Join(", ", keys)}."
            : $"Did you mean '{closest}'?";
    }

    private static int EditDistance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = current;
        }

        return previous[b.Length];
    }

}
