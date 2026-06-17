using Tomix.App.Mutations;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Mv;

public sealed class MoveModelObjectHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public MoveModelObjectHandler(IEnumerable<IModelProvider> providers)
        => _providers = providers.ToList();

    public async Task<TomixResult<MoveModelObjectResult>> HandleAsync(
        MoveModelObjectRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetRename(request.Source, request.Destination, out var newName, out var error))
            return TomixResult<MoveModelObjectResult>.Fail("TOMIX_MOVE_UNSUPPORTED", error);

        var options = new MutationOptions(
            request.Save, request.SaveTo, request.Stage, request.Revert, request.Serialization, request.Force);

        return await MutationRunner.RunAsync(
            _providers, request.Model, options, "mv",
            async (mutator, _, _) =>
            {
                mutator.SetProperty(new ModelObjectSetRequest(
                    request.Source,
                    [new ModelPropertyAssignment("name", newName)],
                    request.Type));

                return (true, $"mv {request.Source} -> {request.Destination}",
                    outcome => new MoveModelObjectResult(
                        NormalizePath(request.Source), NormalizePath(request.Destination),
                        outcome.Saved, outcome.Staged));
            },
            new MoveModelObjectResult(NormalizePath(request.Source), NormalizePath(request.Destination), false, null),
            cancellationToken);
    }

    private static bool TryGetRename(
        string source,
        string destination,
        out string newName,
        out string error)
    {
        var sourcePath = NormalizePath(source);
        var destinationPath = NormalizePath(destination);
        var sourceParent = ParentPath(sourcePath);
        var destinationParent = ParentPath(destinationPath);

        if (!string.Equals(sourceParent, destinationParent, StringComparison.OrdinalIgnoreCase))
        {
            newName = "";
            error = "Moving objects between parents is not supported yet.";
            return false;
        }

        newName = LeafName(destinationPath);
        error = "";
        return true;
    }

    private static string ParentPath(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? "" : path[..index];
    }

    private static string LeafName(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? path : path[(index + 1)..];
    }

    private static string NormalizePath(string path)
        => path.Trim().Trim('/').Replace("'", "", StringComparison.Ordinal);
}
