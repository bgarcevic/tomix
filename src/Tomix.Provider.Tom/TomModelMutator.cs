using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Paths;

namespace Tomix.Provider.Tom;

/// <summary>
/// Entry point for all TOM model mutations, shared by the file, server, and TMDL sessions.
/// A thin facade over the mutation collaborators: <see cref="TomObjectAdder"/> (add),
/// <see cref="TomMutationTargetResolver"/> (path → object), <see cref="TomPropertyApplier"/>
/// (set/rewrite), <see cref="TomTextReplacer"/> (replace), and <see cref="TomRemoveCascade"/>
/// (remove cascades). Every TOM write goes through a <see cref="TomWriter"/>; the public
/// constructor uses <see cref="TomWriter.Untracked"/>, a live session passes its journal's writer.
/// </summary>
public sealed class TomModelMutator
{
    private readonly Database _database;
    private readonly TomWriter _writer;
    private readonly TomObjectAdder _adder;
    private readonly TomMutationTargetResolver _resolver;
    private readonly TomTextReplacer _replacer;

    public TomModelMutator(Database database)
        : this(database, TomWriter.Untracked)
    {
    }

    internal TomModelMutator(Database database, TomWriter writer)
    {
        _database = database;
        _writer = writer;
        _adder = new TomObjectAdder(database, writer);
        _resolver = new TomMutationTargetResolver(database);
        _replacer = new TomTextReplacer(database, writer);
    }

    public ModelObjectMutationResult AddObject(ModelObjectAddRequest request)
        => _adder.AddObject(request);

    public ModelObjectMutationResult SetProperty(ModelObjectSetRequest request)
    {
        if (request.Properties.Count == 0)
            throw new ArgumentException("At least one property assignment is required.", nameof(request));

        if (RefreshPolicyPath.Table(request.Path, request.Type) is { } policyTable)
        {
            var result = new TomRefreshPolicyManager(_database, _writer).SetProperties(policyTable, request.Properties, request.Force);
            var lastProperty = request.Properties[^1];
            return new ModelObjectMutationResult($"{TomMutationPaths.Segment(result.Policy.Table)}/RefreshPolicy",
                true, lastProperty.Property, lastProperty.Value, Policy: result.Policy,
                CreatedExpressions: result.CreatedExpressions);
        }

        var target = _resolver.TryResolveForMutation(request.Path, request.Type)
                     ?? throw TomMutationTargetResolver.NotFound(request.Path);

        ModelPropertyAssignment last = request.Properties[^1];
        foreach (var assignment in request.Properties)
        {
            TomPropertyApplier.ApplyProperty(_writer, target.Target, assignment);
            last = assignment;
        }

        return new ModelObjectMutationResult(
            target.Display,
            Changed: true,
            Property: last.Property,
            Value: last.Value);
    }

    public ModelExpressionRewriteResult RewriteExpressions(IReadOnlyList<ModelExpressionEdit> edits)
    {
        foreach (var edit in edits)
        {
            var resolved = _resolver.TryResolveForMutation(edit.Path, edit.Kind)
                           ?? throw TomMutationTargetResolver.NotFound(edit.Path);
            TomPropertyApplier.ApplyExpressionEdit(_writer, resolved.Target, edit);
        }

        return new ModelExpressionRewriteResult(edits.Count);
    }

    public ModelObjectMutationResult MoveObject(ModelObjectMoveRequest request)
    {
        var resolved = _resolver.TryResolveForMutation(request.Path, request.Type)
                       ?? throw TomMutationTargetResolver.NotFound(request.Path);

        if (resolved.Target is not Measure measure || resolved.Parent is not Table sourceTable)
            throw new NotSupportedException(
                "Only measures can move between tables; columns, hierarchies, partitions, and "
                + "other table children are bound to their table's data.");

        var targetTable = _database.Model.Tables.FirstOrDefault(
                              t => string.Equals(t.Name, request.NewParent, StringComparison.OrdinalIgnoreCase))
                          ?? throw new InvalidOperationException($"Destination table not found: {request.NewParent}");

        // Measure names resolve unqualified in DAX, so they are unique model-wide, not per-table.
        var newName = request.NewName ?? measure.Name;
        if (_database.Model.Tables.SelectMany(t => t.Measures)
                .FirstOrDefault(m => m != measure && string.Equals(m.Name, newName, StringComparison.OrdinalIgnoreCase))
            is { } collision)
            throw new InvalidOperationException(
                $"A measure named '{newName}' already exists in table '{collision.Table.Name}'.");

        // TOM refuses to re-attach a removed object, so the move is clone → detach original →
        // attach clone, and the clone takes over the original's ID. Clone() deep-copies children
        // (KPI, annotations, detail rows), but object-identity references — perspective
        // membership and translations — point at the original and must be captured first and
        // re-created against the clone. The clone is not attached yet, so it is edited directly.
        var clone = measure.Clone();
        clone.Name = newName;
        if (request.NewDisplayFolder is not null)
            clone.DisplayFolder = request.NewDisplayFolder;
        _writer.Rebind(measure, clone);

        var memberships = new List<Perspective>();
        foreach (var perspective in _database.Model.Perspectives)
        {
            var oldEntry = perspective.PerspectiveTables.FirstOrDefault(pt => pt.Table == sourceTable);
            if (oldEntry?.PerspectiveMeasures.FirstOrDefault(pm => pm.Measure == measure) is { } membership)
            {
                _writer.Detach(oldEntry.PerspectiveMeasures, membership);
                memberships.Add(perspective);
            }
        }

        var translations = new List<(Culture Culture, TranslatedProperty Property, string Value)>();
        foreach (var culture in _database.Model.Cultures)
        {
            foreach (var translation in culture.ObjectTranslations
                         .Where(t => ReferenceEquals(t.Object, measure)).ToList())
            {
                _writer.Detach(culture.ObjectTranslations, translation);
                translations.Add((culture, translation.Property, translation.Value));
            }
        }

        _writer.Detach(sourceTable.Measures, measure);
        _writer.Attach(targetTable.Measures, clone);

        foreach (var perspective in memberships)
        {
            var entry = perspective.PerspectiveTables.FirstOrDefault(pt => pt.Table == targetTable);
            if (entry is null)
            {
                entry = new PerspectiveTable { Table = targetTable };
                _writer.Attach(perspective.PerspectiveTables, entry);
            }

            _writer.Attach(entry.PerspectiveMeasures, new PerspectiveMeasure { Measure = clone });
        }

        foreach (var (culture, property, value) in translations)
            _writer.Attach(culture.ObjectTranslations, new ObjectTranslation { Object = clone, Property = property, Value = value });

        return new ModelObjectMutationResult($"{targetTable.Name}/{clone.Name}", Changed: true);
    }

    public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request)
    {
        if (RefreshPolicyPath.Table(request.Path, request.Type) is { } policyTable)
        {
            var manager = new TomRefreshPolicyManager(_database, _writer);
            var remaining = manager.Get(policyTable)?.PolicyPartitions;
            var result = manager.Remove(policyTable, request.IfExists);
            return result with
            {
                Path = $"{TomMutationPaths.Segment(policyTable)}/RefreshPolicy",
                RemainingPolicyPartitions = remaining is { Count: > 0 } ? remaining : null
            };
        }

        var target = _resolver.TryResolveForMutation(request.Path, request.Type);
        if (target is null)
        {
            if (request.IfExists)
                return new ModelObjectMutationResult(TomMutationPaths.NormalizePath(request.Path), Changed: false, Reason: "not_found");

            throw TomMutationTargetResolver.NotFound(request.Path);
        }

        var cascade = RemoveResolvedObject(target);
        return new ModelObjectMutationResult(
            target.Display, Changed: true,
            CascadeRemoved: cascade.Count > 0 ? cascade : null);
    }

    public ModelReplaceResult ReplaceText(ModelReplaceRequest request)
        => _replacer.Replace(request);

    private IReadOnlyList<string> RemoveResolvedObject(TomResolvedObject resolved)
    {
        switch (resolved.Target)
        {
            case Table table:
                {
                    var cascade = TomRemoveCascade.ForTable(_writer, table);
                    _writer.Detach(_database.Model.Tables, table);
                    return cascade;
                }

            case Measure measure when resolved.Parent is Table table:
                {
                    var cascade = TomRemoveCascade.ForMeasure(_writer, measure);
                    _writer.Detach(table.Measures, measure);
                    return cascade;
                }

            case Column column when resolved.Parent is Table table:
                {
                    var cascade = TomRemoveCascade.ForColumn(_writer, column);
                    _writer.Detach(table.Columns, column);
                    return cascade;
                }

            case Hierarchy hierarchy when resolved.Parent is Table table:
                {
                    var cascade = TomRemoveCascade.ForHierarchy(_writer, hierarchy);
                    _writer.Detach(table.Hierarchies, hierarchy);
                    return cascade;
                }

            case Partition partition when resolved.Parent is Table table:
                if (table.Partitions.Count == 1)
                    throw new InvalidOperationException(
                        $"Cannot remove the last partition of table '{table.Name}'; a table must have at least one partition.");

                _writer.Detach(table.Partitions, partition);
                return [];

            case ModelRole role:
                _writer.Detach(_database.Model.Roles, role);
                return [];

            case SingleColumnRelationship relationship:
                {
                    var cascade = TomRemoveCascade.ForRelationship(_writer, relationship);
                    _writer.Detach(_database.Model.Relationships, relationship);
                    return cascade;
                }

            case Level level when resolved.Parent is Hierarchy hierarchy:
                {
                    var cascade = new List<string>();
                    cascade.AddRange(TomRemoveCascade.ForLevel(_writer, level));
                    _writer.Detach(hierarchy.Levels, level);
                    if (hierarchy.Levels.Count == 0)
                    {
                        var table = hierarchy.Table;
                        cascade.AddRange(TomRemoveCascade.ForHierarchy(_writer, hierarchy));
                        _writer.Detach(table.Hierarchies, hierarchy);
                        cascade.Add($"hierarchy '{table.Name}'[{hierarchy.Name}] (no levels left)");
                    }

                    return cascade;
                }

            case CalculationItem item when resolved.Parent is Table calcGroupTable:
                {
                    var cascade = TomRemoveCascade.ForCalculationItem(_writer, item);
                    _writer.Detach(calcGroupTable.CalculationGroup.CalculationItems, item);
                    return cascade;
                }

            case ModelRoleMember member when resolved.Parent is ModelRole memberRole:
                _writer.Detach(memberRole.Members, member);
                return [];

            case KPI when resolved.Parent is Measure kpiMeasure:
                _writer.Set(kpiMeasure, p => p.KPI, null);
                return [];

            case TablePermission permission when resolved.Parent is ModelRole permissionRole:
                _writer.Detach(permissionRole.TablePermissions, permission);
                return [];

            case Calendar calendar when resolved.Parent is Table calendarTable:
                _writer.Detach(calendarTable.Calendars, calendar);
                return [];

            case Perspective perspective:
                _writer.Detach(_database.Model.Perspectives, perspective);
                return [];

            case Culture culture:
                _writer.Detach(_database.Model.Cultures, culture);
                return [];

            case NamedExpression expression:
                _writer.Detach(_database.Model.Expressions, expression);
                return [];

            case Function function:
                _writer.Detach(_database.Model.Functions, function);
                return [];

            case DataSource dataSource:
                {
                    // M partitions can reference a data source by name inside their query text,
                    // which no structural sweep can see — but a QueryPartitionSource binding is
                    // explicit and would fail validation the moment the source disappears.
                    var referencing = _database.Model.Tables
                        .SelectMany(t => t.Partitions
                            .Where(p => p.Source is QueryPartitionSource query && query.DataSource == dataSource)
                            .Select(p => $"{t.Name}/{p.Name}"))
                        .ToList();
                    if (referencing.Count > 0)
                        throw new InvalidOperationException(
                            $"Cannot remove data source '{dataSource.Name}'; it is used by partition(s): "
                            + $"{string.Join(", ", referencing)}. Repoint or remove those partitions first.");

                    _writer.Detach(_database.Model.DataSources, dataSource);
                    return [];
                }

            default:
                throw new NotSupportedException("Removing this object type is not supported.");
        }
    }
}
