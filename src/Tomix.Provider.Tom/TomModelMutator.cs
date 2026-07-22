using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;

namespace Tomix.Provider.Tom;

/// <summary>
/// Entry point for all TOM model mutations, shared by the file, server, and TMDL sessions.
/// A thin facade over the mutation collaborators: <see cref="TomObjectAdder"/> (add),
/// <see cref="TomMutationTargetResolver"/> (path → object), <see cref="TomPropertyApplier"/>
/// (set/rewrite), <see cref="TomTextReplacer"/> (replace), and <see cref="TomRemoveCascade"/>
/// (remove cascades).
/// </summary>
public sealed class TomModelMutator
{
    private readonly Database _database;
    private readonly TomObjectAdder _adder;
    private readonly TomMutationTargetResolver _resolver;
    private readonly TomTextReplacer _replacer;

    public TomModelMutator(Database database)
    {
        _database = database;
        _adder = new TomObjectAdder(database);
        _resolver = new TomMutationTargetResolver(database);
        _replacer = new TomTextReplacer(database);
    }

    public ModelObjectMutationResult AddObject(ModelObjectAddRequest request)
        => _adder.AddObject(request);

    public ModelObjectMutationResult SetProperty(ModelObjectSetRequest request)
    {
        if (request.Properties.Count == 0)
            throw new ArgumentException("At least one property assignment is required.", nameof(request));

        var target = _resolver.TryResolveForMutation(request.Path, request.Type)
                     ?? throw TomMutationTargetResolver.NotFound(request.Path);

        ModelPropertyAssignment last = request.Properties[^1];
        foreach (var assignment in request.Properties)
        {
            TomPropertyApplier.ApplyProperty(target.Target, assignment);
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
            TomPropertyApplier.ApplyExpressionEdit(resolved.Target, edit);
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
        // attach clone. Clone() deep-copies children (KPI, annotations, detail rows), but
        // object-identity references — perspective membership and translations — point at the
        // original and must be captured first and re-created against the clone.
        var clone = measure.Clone();
        clone.Name = newName;
        if (request.NewDisplayFolder is not null)
            clone.DisplayFolder = request.NewDisplayFolder;

        var memberships = new List<Perspective>();
        foreach (var perspective in _database.Model.Perspectives)
        {
            var oldEntry = perspective.PerspectiveTables.FirstOrDefault(pt => pt.Table == sourceTable);
            if (oldEntry?.PerspectiveMeasures.FirstOrDefault(pm => pm.Measure == measure) is { } membership)
            {
                oldEntry.PerspectiveMeasures.Remove(membership);
                memberships.Add(perspective);
            }
        }

        var translations = new List<(Culture Culture, TranslatedProperty Property, string Value)>();
        foreach (var culture in _database.Model.Cultures)
        {
            foreach (var translation in culture.ObjectTranslations
                         .Where(t => ReferenceEquals(t.Object, measure)).ToList())
            {
                culture.ObjectTranslations.Remove(translation);
                translations.Add((culture, translation.Property, translation.Value));
            }
        }

        sourceTable.Measures.Remove(measure);
        targetTable.Measures.Add(clone);

        foreach (var perspective in memberships)
        {
            var entry = perspective.PerspectiveTables.FirstOrDefault(pt => pt.Table == targetTable);
            if (entry is null)
            {
                entry = new PerspectiveTable { Table = targetTable };
                perspective.PerspectiveTables.Add(entry);
            }

            entry.PerspectiveMeasures.Add(new PerspectiveMeasure { Measure = clone });
        }

        foreach (var (culture, property, value) in translations)
            culture.ObjectTranslations.Add(new ObjectTranslation { Object = clone, Property = property, Value = value });

        return new ModelObjectMutationResult($"{targetTable.Name}/{clone.Name}", Changed: true);
    }

    public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request)
    {
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
                    var cascade = TomRemoveCascade.ForTable(table);
                    _database.Model.Tables.Remove(table);
                    return cascade;
                }

            case Measure measure when resolved.Parent is Table table:
                {
                    var cascade = TomRemoveCascade.ForMeasure(measure);
                    table.Measures.Remove(measure);
                    return cascade;
                }

            case Column column when resolved.Parent is Table table:
                {
                    var cascade = TomRemoveCascade.ForColumn(column);
                    table.Columns.Remove(column);
                    return cascade;
                }

            case Hierarchy hierarchy when resolved.Parent is Table table:
                {
                    var cascade = TomRemoveCascade.ForHierarchy(hierarchy);
                    table.Hierarchies.Remove(hierarchy);
                    return cascade;
                }

            case Partition partition when resolved.Parent is Table table:
                if (table.Partitions.Count == 1)
                    throw new InvalidOperationException(
                        $"Cannot remove the last partition of table '{table.Name}'; a table must have at least one partition.");

                table.Partitions.Remove(partition);
                return [];

            case ModelRole role:
                _database.Model.Roles.Remove(role);
                return [];

            case SingleColumnRelationship relationship:
                {
                    var cascade = TomRemoveCascade.ForRelationship(relationship);
                    _database.Model.Relationships.Remove(relationship);
                    return cascade;
                }

            case Level level when resolved.Parent is Hierarchy hierarchy:
                {
                    var cascade = new List<string>();
                    cascade.AddRange(TomRemoveCascade.ForLevel(level));
                    hierarchy.Levels.Remove(level);
                    if (hierarchy.Levels.Count == 0)
                    {
                        var table = hierarchy.Table;
                        cascade.AddRange(TomRemoveCascade.ForHierarchy(hierarchy));
                        table.Hierarchies.Remove(hierarchy);
                        cascade.Add($"hierarchy '{table.Name}'[{hierarchy.Name}] (no levels left)");
                    }

                    return cascade;
                }

            case CalculationItem item when resolved.Parent is Table calcGroupTable:
                {
                    var cascade = TomRemoveCascade.ForCalculationItem(item);
                    calcGroupTable.CalculationGroup.CalculationItems.Remove(item);
                    return cascade;
                }

            case ModelRoleMember member when resolved.Parent is ModelRole memberRole:
                memberRole.Members.Remove(member);
                return [];

            case KPI when resolved.Parent is Measure kpiMeasure:
                kpiMeasure.KPI = null;
                return [];

            case TablePermission permission when resolved.Parent is ModelRole permissionRole:
                permissionRole.TablePermissions.Remove(permission);
                return [];

            case Calendar calendar when resolved.Parent is Table calendarTable:
                calendarTable.Calendars.Remove(calendar);
                return [];

            case Perspective perspective:
                _database.Model.Perspectives.Remove(perspective);
                return [];

            case Culture culture:
                _database.Model.Cultures.Remove(culture);
                return [];

            case NamedExpression expression:
                _database.Model.Expressions.Remove(expression);
                return [];

            case Function function:
                _database.Model.Functions.Remove(function);
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

                    _database.Model.DataSources.Remove(dataSource);
                    return [];
                }

            default:
                throw new NotSupportedException("Removing this object type is not supported.");
        }
    }
}
