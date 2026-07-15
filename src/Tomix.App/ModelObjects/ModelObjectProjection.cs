using Tomix.Core.Models;

namespace Tomix.App.ModelObjects;

internal static class ModelObjectProjection
{
    public static IReadOnlyList<ModelObject> Flatten(ModelSnapshot snapshot)
    {
        var objects = new List<ModelObject>();

        void Walk(IEnumerable<ModelObject> nodes)
        {
            foreach (var node in nodes)
            {
                objects.Add(node);
                Walk(node.Children);
            }
        }

        Walk(snapshot.Objects);
        return objects;
    }

    public static string KindLabel(ModelObjectKind kind) => kind switch
    {
        ModelObjectKind.RoleMember => "RoleMember",
        _ => kind.ToString()
    };
}
