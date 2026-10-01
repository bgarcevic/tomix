namespace Tomix.Core.Models;

/// <summary>
/// An immutable snapshot of a live session at one <see cref="Version"/>, with lookups between
/// object IDs and paths. Results computed from it should carry <see cref="Version"/> so clients
/// can drop answers that are out of date.
/// </summary>
public sealed class LiveModelSnapshot
{
    private readonly Lazy<ModelObjectIndex> _index;

    public LiveModelSnapshot(long version, ModelSnapshot snapshot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        ArgumentNullException.ThrowIfNull(snapshot);
        Version = version;
        Snapshot = snapshot;
        _index = new Lazy<ModelObjectIndex>(() => ModelObjectIndex.Build(snapshot));
    }

    public long Version { get; }

    public ModelSnapshot Snapshot { get; }

    /// <summary>ID and path lookups, built on first use.</summary>
    public ModelObjectIndex Index => _index.Value;
}

/// <summary>
/// Lookups in both directions between <see cref="ObjectId"/>s and the canonical paths
/// (<see cref="ModelObject.Path"/>) of a snapshot whose objects carry IDs. Paths compare
/// case-insensitively, as model object names do. Resolving the other path forms users type
/// (DAX references, wildcards, keywords) is <see cref="Paths.ModelObjectSelector"/>'s job; its
/// results carry the ID.
/// </summary>
public sealed class ModelObjectIndex
{
    private readonly Dictionary<ObjectId, ModelObject> _byId;
    private readonly Dictionary<string, ObjectId> _byPath;

    private ModelObjectIndex(Dictionary<ObjectId, ModelObject> byId, Dictionary<string, ObjectId> byPath)
    {
        _byId = byId;
        _byPath = byPath;
    }

    /// <summary>The number of objects that carry an ID.</summary>
    public int Count => _byId.Count;

    /// <summary>Indexes every object in <paramref name="snapshot"/> that has an ID. When two
    /// objects share a path, the first in tree order owns it.</summary>
    /// <exception cref="ArgumentException">Two objects carry the same ID.</exception>
    public static ModelObjectIndex Build(ModelSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var byId = new Dictionary<ObjectId, ModelObject>();
        var byPath = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);

        var pending = new Stack<ModelObject>(snapshot.Objects.Reverse());
        while (pending.TryPop(out var node))
        {
            if (node.Id is { } id)
            {
                if (!byId.TryAdd(id, node))
                    throw new ArgumentException($"Object ID {id} appears on both '{byId[id].Path}' and '{node.Path}'.", nameof(snapshot));

                byPath.TryAdd(node.Path, id);
            }

            for (var i = node.Children.Count - 1; i >= 0; i--)
                pending.Push(node.Children[i]);
        }

        return new ModelObjectIndex(byId, byPath);
    }

    public bool TryGetObject(ObjectId id, out ModelObject obj)
        => _byId.TryGetValue(id, out obj!);

    public bool TryGetPath(ObjectId id, out string path)
    {
        if (_byId.TryGetValue(id, out var obj))
        {
            path = obj.Path;
            return true;
        }

        path = string.Empty;
        return false;
    }

    public bool TryGetId(string path, out ObjectId id)
        => _byPath.TryGetValue(path, out id);
}
