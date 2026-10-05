using Tomix.App.Add;
using Tomix.App.Bpa;
using Tomix.App.Find;
using Tomix.App.Format;
using Tomix.App.Get;
using Tomix.App.Models;
using Tomix.App.Mutations;
using Tomix.App.Mv;
using Tomix.App.Replace;
using Tomix.App.Rm;
using Tomix.App.Set;
using Tomix.App.State;
using Tomix.App.Tests.Support;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;

namespace Tomix.App.Tests;

/// <summary>
/// The same handlers the one-shot CLI runs, served by one open live session (#345): reads see
/// earlier edits without a reload, edits stay in memory until a save, and a failed request
/// leaves the model as it found it.
/// </summary>
public sealed class LiveSessionSourceTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Handlers_share_one_live_session_without_reloading_or_saving()
    {
        await using var live = await Live.OpenAsync();
        var model = live.Model;
        var totalSalesId = await live.IdAsync("Sales/Total Sales");

        var get = await new GetModelHandler(live.Source).HandleAsync(new GetModelRequest(model, "Sales/Total Sales", null, null), None);
        Assert.True(get.Success);
        Assert.Equal("SUM ( Sales[Amount] )", get.Data!.Object!.Object.Expression);

        var ls = await new GetModelHandler(live.Source).HandleAsync(new GetModelRequest(model, null, null, null), None);
        Assert.Equal(["Customers", "Products", "Sales"], ls.Data!.List!.Objects.Select(o => o.Name).Order());

        var set = await live.Mutate(stores => new SetModelPropertyHandler(live.Source, stores).HandleAsync(
            new SetModelPropertyRequest(model, "Sales/Total Sales", [new ModelPropertyAssignment("expression", "SUM ( Sales[Amount] ) * 2")],
                null, Save: false, SaveTo: null, Serialization: ""), None));
        Assert.Equal(MutationStatus.Applied, set.Status);
        Assert.Equal(1, live.Session.Version);

        // The next read sees the edit: it reads the session's model, not the folder.
        get = await new GetModelHandler(live.Source).HandleAsync(new GetModelRequest(model, "Sales/Total Sales", null, null), None);
        Assert.Equal("SUM ( Sales[Amount] ) * 2", get.Data!.Object!.Object.Expression);

        var add = await live.Mutate(stores => new AddModelObjectHandler(live.Source, stores).HandleAsync(
            new AddModelObjectRequest(model, "Sales/Live Probe", "measure", "[Total Sales] + 1", [], IfNotExists: false,
                Save: false, SaveTo: null, Serialization: "", Force: false), None));
        Assert.Equal(MutationStatus.Applied, add.Status);

        var deps = await new GetModelHandler(live.Source).HandleAsync(
            new GetModelRequest(model, "Sales/Total Sales", null, null, GetMode.Deps), None);
        Assert.Contains(deps.Data!.Deps!.Downstream, d => d.Path == "Sales/Live Probe");

        var replace = await live.Mutate(stores => new ReplaceModelTextHandler(live.Source, stores).HandleAsync(
            new ReplaceModelTextRequest(model, "DISTINCTCOUNT(", "DISTINCTCOUNTNOBLANK(", "expressions", Regex: false, CaseSensitive: true,
                Save: false, SaveTo: null, Serialization: "", Force: false), None));
        Assert.Equal(MutationStatus.Applied, replace.Status);

        var find = await new FindModelHandler(live.Source).HandleAsync(
            new FindModelRequest(model, "DISTINCTCOUNTNOBLANK", "expressions", Regex: false, CaseSensitive: true), None);
        Assert.Equal("Sales/Distinct Customers", Assert.Single(find.Data!.Matches).Path);

        var mv = await live.Mutate(stores => new MoveModelObjectHandler(live.Source, stores).HandleAsync(
            new MoveModelObjectRequest(model, "Sales/Live Probe", "Customers/Live Probe", null,
                Save: false, SaveTo: null, Serialization: ""), None));
        Assert.Equal(MutationStatus.Applied, mv.Status);

        var format = await live.Mutate(stores => new FormatModelHandler(live.Source, new OfflineDaxFormatterClient(), stores).HandleAsync(
            new FormatModelRequest(model, Expression: null, Path: "Sales/Order Count", Language: "", Type: null, Long: false,
                Save: false, SaveTo: null), None));
        Assert.Equal(MutationStatus.Applied, Assert.IsAssignableFrom<MutationResult>(format).Status);

        var rm = await live.Mutate(stores => new RemoveModelObjectHandler(live.Source, stores).HandleAsync(
            new RemoveModelObjectRequest(model, "Customers/Live Probe", null, IfExists: false,
                Save: false, SaveTo: null, Serialization: "", Force: false), None));
        Assert.Equal(MutationStatus.Applied, rm.Status);

        var bpa = await new BpaRunHandler(live.Source, live.Config.Stores, new BpaUserRuleState(live.Config.Path), live.Config.Path)
            .HandleAsync(new BpaRunRequest(model), None);
        Assert.NotNull(bpa.Data);
        Assert.True(bpa.Data.RulesEvaluated > 0);

        // Six edits, six versions; reads and the BPA run committed nothing.
        Assert.Equal(6, live.Session.Version);
        Assert.Equal("COUNTROWS ( Sales )", await live.ExpressionAsync("Sales/Order Count"));
        Assert.Contains("DISTINCTCOUNTNOBLANK(", await live.ExpressionAsync("Sales/Distinct Customers"));
        Assert.Null(await live.ExpressionAsync("Customers/Live Probe"));
        // Same object, same ID: every request ran on the one model the session loaded.
        Assert.Equal(totalSalesId, await live.IdAsync("Sales/Total Sales"));
        // Nothing was saved.
        Assert.True(live.Session.IsDirty);
        Assert.Equal(live.FilesAtOpen, live.Files());
    }

    [Fact]
    public async Task A_request_that_fails_partway_leaves_the_live_model_untouched()
    {
        await using var live = await Live.OpenAsync();

        // The expression is set before the unknown property fails the request.
        var result = await new SetModelPropertyHandler(live.Source, live.Config.Stores).HandleAsync(
            new SetModelPropertyRequest(live.Model, "Sales/Total Sales",
                [new ModelPropertyAssignment("expression", "0"), new ModelPropertyAssignment("noSuchProperty", "x")],
                null, Save: false, SaveTo: null, Serialization: ""), None);

        Assert.False(result.Success);
        Assert.Equal("SUM ( Sales[Amount] )", await live.ExpressionAsync("Sales/Total Sales"));
        Assert.Equal(0, live.Session.Version);
        Assert.False(live.Session.IsDirty);
    }

    [Fact]
    public async Task Save_on_a_live_session_writes_the_folder_and_leaves_it_clean()
    {
        await using var live = await Live.OpenAsync();

        var set = await live.Mutate(stores => new SetModelPropertyHandler(live.Source, stores).HandleAsync(
            new SetModelPropertyRequest(live.Model, "Sales/Total Sales", [new ModelPropertyAssignment("expression", "42")],
                null, Save: true, SaveTo: null, Serialization: ""), None));

        Assert.Equal(MutationStatus.Saved, set.Status);
        Assert.Equal(1, live.Session.Version);
        Assert.False(live.Session.IsDirty);
        Assert.Contains("measure 'Total Sales' = 42", File.ReadAllText(Path.Combine(live.Model.Value, "tables", "Sales.tmdl")));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Staging_is_rejected_on_a_live_session(bool stage, bool revert)
    {
        await using var live = await Live.OpenAsync();

        var result = await new SetModelPropertyHandler(live.Source, live.Config.Stores).HandleAsync(
            new SetModelPropertyRequest(live.Model, "Sales/Total Sales", revert ? [] : [new ModelPropertyAssignment("expression", "0")],
                null, Save: false, SaveTo: null, Serialization: "", Stage: stage, Revert: revert), None);

        Assert.Equal("TOMIX_SESSION_STAGE_UNSUPPORTED", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0, live.Session.Version);
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(live.Config.Path, "*", SearchOption.AllDirectories),
            p => p.Contains("staging", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_live_session_serves_only_its_own_model()
    {
        await using var live = await Live.OpenAsync();
        using var other = SampleModel.CopyToTemp();

        var result = await new GetModelHandler(live.Source).HandleAsync(
            new GetModelRequest(new ModelReference(other.Path), "Sales/Total Sales", null, null), None);

        Assert.Equal("TOMIX_SESSION_MODEL_MISMATCH", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void The_same_folder_matches_however_it_is_spelled()
    {
        using var dir = new TempDir();
        var held = new ModelReference(dir.Path);

        Assert.True(LiveSessionSource.SameModel(new ModelReference(dir.Path + Path.DirectorySeparatorChar), held));
        Assert.True(LiveSessionSource.SameModel(new ModelReference(Path.Combine(dir.Path, "sub", "..")), held));
        Assert.False(LiveSessionSource.SameModel(new ModelReference(dir.Combine("sub")), held));
        Assert.False(LiveSessionSource.SameModel(new ModelReference("powerbi://api.powerbi.com/v1.0/myorg/ws", "db"), held));
        Assert.True(LiveSessionSource.SameModel(
            new ModelReference("powerbi://api.powerbi.com/v1.0/myorg/WS/", "DB"),
            new ModelReference("powerbi://api.powerbi.com/v1.0/myorg/ws", "db")));
    }

    /// <summary>A writable copy of the sample, open as a live session the way a shell host would hold it.</summary>
    private sealed class Live : IAsyncDisposable
    {
        private readonly TempDir _dir;

        private Live(TempDir dir, ILiveModelSession session)
        {
            _dir = dir;
            Session = session;
            Model = session.Reference;
            Source = new LiveSessionSource(session, new LiveLeaseOptions("shell"));
            FilesAtOpen = Files();
        }

        public ILiveModelSession Session { get; }

        public LiveSessionSource Source { get; }

        public ModelReference Model { get; }

        public TempConfigDir Config { get; } = new();

        public SortedDictionary<string, string> FilesAtOpen { get; }

        public static async Task<Live> OpenAsync()
        {
            var dir = SampleModel.CopyToTemp();
            return new Live(dir, await new TmdlModelProvider().OpenLiveAsync(new ModelReference(dir.Path), None));
        }

        /// <summary>Runs a mutation handler and returns its result, failing the test on a diagnostic.</summary>
        public async Task<T> Mutate<T>(Func<MutationStores, Task<Tomix.Core.Results.TomixResult<T>>> run)
        {
            var result = await run(Config.Stores);
            Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
            return result.Data!;
        }

        public async Task<string?> ExpressionAsync(string path) => (await FindAsync(path))?.Expression;

        public async Task<ObjectId?> IdAsync(string path) => (await FindAsync(path))?.Id;

        public SortedDictionary<string, string> Files()
            => new(Directory.EnumerateFiles(_dir.Path, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetRelativePath(_dir.Path, f), File.ReadAllText), StringComparer.Ordinal);

        private async Task<ModelObject?> FindAsync(string path)
        {
            var snapshot = await Session.GetLiveSnapshotAsync(None);
            var pending = new Stack<ModelObject>(snapshot.Snapshot.Objects);
            while (pending.TryPop(out var obj))
            {
                if (obj.Path == path)
                    return obj;
                foreach (var child in obj.Children)
                    pending.Push(child);
            }
            return null;
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            Config.Dispose();
            _dir.Dispose();
        }
    }
}
