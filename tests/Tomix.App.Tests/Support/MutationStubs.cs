using Tomix.Core.Models;

namespace Tomix.App.Tests.Support;

/// <summary>
/// A mutation- and rewrite-capable session over a fixed snapshot, plus the provider that opens it,
/// for handlers that rename or move objects and fix up the DAX that references them.
/// </summary>
/// <remarks>
/// MoveModelObjectHandlerTests and RenameReferenceFixupTests each carried their own copy of this
/// pair. The provider was byte-identical; the sessions differed only in which calls they recorded,
/// so this one records the union.
/// </remarks>
public static class MutationStubs
{
    /// <summary>
    /// Sales with measures Base (<c>1</c>) and Derived (<c>[Base] * 2</c>) — the minimum shape for
    /// "renaming Base must rewrite Derived".
    /// </summary>
    public static ModelSnapshot BaseAndDerived() => new("M", 1601,
    [
        Table("Sales"),
        Measure("Base", "Sales/Base", "1"),
        Measure("Derived", "Sales/Derived", "[Base] * 2")
    ]);

    public static ModelObject Table(string name)
        => new(name, ModelObjectKind.Table, name,
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null, Children: []);

    public static ModelObject Measure(
        string name, string path, string expression, IReadOnlyDictionary<string, string>? properties = null)
        => new(name, ModelObjectKind.Measure, path,
            Detail: null, Expression: expression, Description: null, Hidden: false, SourceColumn: null,
            Children: [], Properties: properties);

    /// <summary>Opens any reference with <paramref name="session"/>.</summary>
    public sealed class Provider(IModelSession session) : IModelProvider
    {
        public bool CanOpen(ModelReference reference) => true;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken cancellationToken)
            => Task.FromResult(session);
    }

    /// <summary>
    /// Accepts every mutation and records what it was asked, so a test can assert both the calls
    /// made and their order.
    /// </summary>
    public class SnapshotSession(ModelSnapshot snapshot)
        : IModelSession, IModelMutationSession, IExpressionRewriteSession
    {
        public bool SetPropertyCalled { get; private set; }

        public string? LastSetValue { get; private set; }

        public IReadOnlyList<ModelPropertyAssignment>? LastSetProperties { get; private set; }

        public IReadOnlyList<ModelExpressionEdit>? LastRewrites { get; private set; }

        /// <summary>
        /// True when <see cref="RewriteExpressions"/> ran before any <see cref="SetProperty"/>.
        /// The rename must fix up references while the old name still resolves.
        /// </summary>
        public bool RewriteCameBeforeSetProperty { get; private set; }

        public bool SnapshotRequested { get; private set; }

        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ModelSummary("stub", 1601, 1, 2, 0, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            SnapshotRequested = true;
            return Task.FromResult(snapshot);
        }

        public ValueTask DisposeAsync()
        {
            // Nothing to release; SuppressFinalize keeps CA1816 satisfied on an unsealed type
            // (MoveCapableSnapshotSession derives from this one).
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }

        public ModelObjectMutationResult AddObject(ModelObjectAddRequest request)
            => new(request.Path, Changed: true);

        public ModelObjectMutationResult SetProperty(ModelObjectSetRequest request)
        {
            SetPropertyCalled = true;
            LastSetValue = request.Properties[^1].Value;
            LastSetProperties = request.Properties;
            return new ModelObjectMutationResult(
                request.Path, Changed: true,
                Property: request.Properties[^1].Property, Value: request.Properties[^1].Value);
        }

        public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request)
            => new(request.Path, Changed: true);

        public ModelReplaceResult ReplaceText(ModelReplaceRequest request) => new(0, []);

        public ModelExpressionRewriteResult RewriteExpressions(IReadOnlyList<ModelExpressionEdit> edits)
        {
            LastRewrites = edits;
            RewriteCameBeforeSetProperty = !SetPropertyCalled;
            return new ModelExpressionRewriteResult(edits.Count);
        }

        public Task<ModelExportResult> SaveAsync(
            string? outputPath, string serialization, bool force, CancellationToken cancellationToken)
            => Task.FromResult(new ModelExportResult(outputPath ?? "/local/model", serialization));
    }

    /// <summary>A <see cref="SnapshotSession"/> that also claims the native-move capability.</summary>
    public sealed class MoveCapableSnapshotSession(ModelSnapshot snapshot) : SnapshotSession(snapshot), IObjectMoveSession
    {
        public ModelObjectMoveRequest? LastMove { get; private set; }

        public ModelObjectMutationResult MoveObject(ModelObjectMoveRequest request)
        {
            LastMove = request;
            return new ModelObjectMutationResult($"{request.NewParent}/{request.NewName}", Changed: true);
        }
    }
}
