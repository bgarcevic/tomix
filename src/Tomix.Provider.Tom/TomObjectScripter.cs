using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;

namespace Tomix.Provider.Tom;

/// <summary>
/// <see cref="IObjectScriptSession"/> for every TOM-backed session: resolves a snapshot path the
/// way mutations do, then hands the object to TOM's own serializers, so a fragment carries every
/// property a model file would (format strings, display folders, descriptions, lineage tags,
/// annotations, child objects).
/// </summary>
public static class TomObjectScripter
{
    public static string Script(Database database, string path, ModelObjectKind kind, ObjectScriptFormat format)
    {
        var target = Resolve(database, path, kind);
        return format switch
        {
            // Qualified: a table child comes wrapped in 'ref table', so it pastes into a table file.
            ObjectScriptFormat.Tmdl => TmdlSerializer.SerializeObject(target, qualifyObject: true),
            ObjectScriptFormat.Json => JsonSerializer.SerializeObject(target),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
        };
    }

    private static MetadataObject Resolve(Database database, string path, ModelObjectKind kind)
    {
        // The model root is the database in mutation paths; its fragment is the model itself.
        if (kind == ModelObjectKind.Model)
            return database.Model;

        // Mutation paths do not tell a calculated column from a data column.
        var type = kind == ModelObjectKind.CalculatedColumn ? ModelObjectKind.Column : kind;
        return new TomMutationTargetResolver(database).TryResolveForMutation(path, type)?.Target as MetadataObject
               ?? throw TomMutationTargetResolver.NotFound(path);
    }
}
