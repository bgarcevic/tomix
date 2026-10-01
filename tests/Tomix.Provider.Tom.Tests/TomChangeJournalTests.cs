using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using static Tomix.Provider.Tom.Tests.TestModels;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// The guarantees of <see cref="TomChangeJournal"/> the oracles do not pin down on their own:
/// IDs that survive instance swaps, the exact shape of folded changes, savepoints, and the
/// provider-neutral entry fields merge (#374) replays.
/// </summary>
public sealed class TomChangeJournalTests
{
    [Fact]
    public void Move_KeepsTheIdsOfTheMeasureAndItsKpi()
    {
        var (journal, before) = Open(JournalFixture.Rich());

        var changes = Apply(journal, m => m.MoveObject(Move("Sales/Revenue", "Metrics")));

        var after = Index(journal);
        var change = Assert.Single(changes, c => c.Id == before["Sales/Revenue"]);
        Assert.Equal(ModelChangeKind.Moved, change.Change);
        Assert.Equal("Metrics/Revenue", change.Path);
        Assert.Equal("Sales/Revenue", change.OldPath);
        Assert.Equal(before["Sales/Revenue"], after["Metrics/Revenue"]);
        Assert.Equal(before["Sales/Revenue/KPI"], after["Metrics/Revenue/KPI"]);
    }

    [Fact]
    public void RenamingARoleMember_KeepsItsIdThroughTheReplacement()
    {
        var (journal, before) = Open(JournalFixture.Rich());

        var changes = Apply(journal, m => m.SetProperty(Set("Readers/user@contoso.com", "name", "other@contoso.com")));

        var change = Assert.Single(changes);
        Assert.Equal(ModelChangeKind.Renamed, change.Change);
        Assert.Equal(before["Roles/Readers/user@contoso.com"], change.Id);
        Assert.Equal("Roles/Readers/other@contoso.com", change.Path);
        Assert.Equal(before["Roles/Readers/user@contoso.com"], Index(journal)["Roles/Readers/other@contoso.com"]);
    }

    [Fact]
    public void RenamingATable_ReportsOnlyTheTable()
    {
        var (journal, before) = Open(JournalFixture.Rich());

        var changes = Apply(journal, m => m.SetProperty(Set("Sales", "name", "Orders")));

        Assert.Equal(new ModelChange(before["Sales"], ModelObjectKind.Table, ModelChangeKind.Renamed, "Orders", OldPath: "Sales"),
            Assert.Single(changes));
    }

    [Fact]
    public void RemovingATable_ReportsTheTableAndItsCascadesButNotItsChildren()
    {
        var (journal, before) = Open(JournalFixture.Rich());

        var changes = Apply(journal, m => m.RemoveObject(Remove("Customer")));

        Assert.Equal(
        [
            (before["Sales/CustomerId"], ModelChangeKind.Modified),
            (before["Relationships/SalesToCustomer"], ModelChangeKind.Removed),
            (before["Roles/Readers/Customer"], ModelChangeKind.Removed),
            (before["Customer"], ModelChangeKind.Removed)
        ], changes.Select(c => (c.Id, c.Change)));
        Assert.Equal(["Variation"], changes[0].Properties);
    }

    [Fact]
    public void AddingAndRemovingInOneTransaction_ReportsNothing()
    {
        var (journal, _) = Open(JournalFixture.Rich());

        var changes = Apply(journal, m =>
        {
            m.AddObject(Add("Sales/Profit", "Measure", "1"));
            m.SetProperty(Set("Sales/Profit", "expression", "2"));
            m.RemoveObject(Remove("Sales/Profit"));
        });

        Assert.Empty(changes);
    }

    [Fact]
    public void WritingAnAttachedObjectOutsideATransaction_Throws()
    {
        var journal = new TomChangeJournal(JournalFixture.Rich());
        var mutator = new TomModelMutator(journal.Database, journal.Writer);

        Assert.Throws<InvalidOperationException>(() => mutator.SetProperty(Set("Sales/Revenue", "expression", "1")));
    }

    [Fact]
    public void WritingTheValueAPropertyAlreadyHas_RecordsNothingAndKeepsTheDatabase()
    {
        var database = JournalFixture.Rich();
        var journal = new TomChangeJournal(database);

        journal.Begin();
        new TomModelMutator(journal.Database, journal.Writer).SetProperty(Set("Sales/Revenue", "expression", "SUM(Sales[Amount])"));
        Assert.Empty(journal.Entries);
        journal.Rollback();

        // No write, so no checkpoint was taken and nothing was swapped in.
        Assert.Same(database, journal.Database);
    }

    [Fact]
    public void RollingBackASavepoint_UndoesOnlyItsOwnWrites()
    {
        var (journal, before) = Open(JournalFixture.Rich());
        journal.Begin();
        Mutator(journal).SetProperty(Set("Sales/Revenue", "expression", "1"));

        journal.Begin();
        Mutator(journal).SetProperty(Set("Sales/Count", "expression", "2"));
        Mutator(journal).RemoveObject(Remove("Customer"));
        journal.Rollback();

        var model = journal.Database.Model;
        Assert.Equal("1", model.Tables["Sales"].Measures["Revenue"].Expression);
        Assert.Equal("COUNTROWS(Sales)", model.Tables["Sales"].Measures["Count"].Expression);
        Assert.NotNull(model.Tables.Find("Customer"));
        var change = Assert.Single(journal.Commit());
        Assert.Equal((before["Sales/Revenue"], ModelChangeKind.Modified), (change.Id, change.Change));
        Assert.Equal(before["Customer"], Index(journal)["Customer"]);
    }

    [Fact]
    public void CommittingASavepoint_FoldsItsChangesIntoTheOuterTransaction()
    {
        var (journal, before) = Open(JournalFixture.Rich());
        journal.Begin();
        journal.Begin();
        Mutator(journal).SetProperty(Set("Sales/Revenue", "expression", "1"));

        Assert.Empty(journal.Commit());
        Assert.Equal(1, journal.Depth);
        var change = Assert.Single(journal.Commit());
        Assert.Equal(before["Sales/Revenue"], change.Id);
        Assert.Equal(["Expression"], change.Properties);
    }

    [Fact]
    public void Entries_DescribeTheWriteWithoutTomReferences()
    {
        var database = JournalFixture.Rich();
        database.Model.Tables["Sales"].Measures["Revenue"].LineageTag = "rev-tag";
        var journal = new TomChangeJournal(database);
        journal.Begin();

        Mutator(journal).SetProperty(Set("Sales/Revenue", "expression", "1"));
        Mutator(journal).SetProperty(Set("Sales/Revenue", "translation:da-DK/caption", "Salg"));
        Mutator(journal).AddObject(Add("Sales/Profit", "Measure", "2"));

        Assert.Collection(journal.Entries,
            set =>
            {
                Assert.Equal((TomJournalOperation.Set, ModelObjectKind.Measure, "Sales/Revenue", "rev-tag"),
                    (set.Operation, set.Kind, set.Path, set.LineageTag));
                Assert.Equal(("Expression", "SUM(Sales[Amount])", "1"), (set.Property, set.Before, set.After));
            },
            translation =>
            {
                Assert.Equal((TomJournalOperation.Set, "Sales/Revenue", "Translation:da-DK/Caption.Value"),
                    (translation.Operation, translation.Path, translation.Property));
                Assert.Equal(("Omsætning", "Salg"), (translation.Before, translation.After));
            },
            add =>
            {
                Assert.Equal((TomJournalOperation.Attach, ModelObjectKind.Measure, "Sales/Profit", null),
                    (add.Operation, add.Kind, add.Path, add.Property));
                Assert.Equal(journal.Ids.GetOrAdd(database.Model.Tables["Sales"]), add.Ancestors[0]);
            });
    }

    [Fact]
    public void RollingBackBySwap_ReplacesTheDatabaseInstance()
    {
        var database = JournalFixture.Rich();
        var journal = new TomChangeJournal(database);
        journal.Begin();
        Mutator(journal).SetProperty(Set("Sales/Revenue", "expression", "1"));

        journal.Rollback();

        Assert.NotSame(database, journal.Database);
        Assert.Equal("SUM(Sales[Amount])", journal.Database.Model.Tables["Sales"].Measures["Revenue"].Expression);
    }

    [Fact]
    public void RollingBackByCopyTo_KeepsTheDatabaseInstance()
    {
        var database = JournalFixture.Rich();
        var journal = new TomChangeJournal(database, TomCheckpointRestore.CopyTo);
        journal.Begin();
        Mutator(journal).RemoveObject(Remove("Sales/Revenue"));

        journal.Rollback();

        Assert.Same(database, journal.Database);
        Assert.NotNull(database.Model.Tables["Sales"].Measures.Find("Revenue"));
    }

    private static (TomChangeJournal Journal, Dictionary<string, ObjectId> Ids) Open(Database database)
    {
        var journal = new TomChangeJournal(database);
        return (journal, Index(journal));
    }

    private static IReadOnlyList<ModelChange> Apply(TomChangeJournal journal, Action<TomModelMutator> mutate)
    {
        journal.Begin();
        mutate(Mutator(journal));
        return journal.Commit();
    }

    private static TomModelMutator Mutator(TomChangeJournal journal) => new(journal.Database, journal.Writer);

    /// <summary>Path → ID for every object in a fresh snapshot of the journal's model.</summary>
    private static Dictionary<string, ObjectId> Index(TomChangeJournal journal)
    {
        var snapshot = TomModelSummarizer.Snapshot(journal.Database, "M", journal.Ids);
        var index = new Dictionary<string, ObjectId>(StringComparer.Ordinal);
        var pending = new Stack<ModelObject>(snapshot.Objects);
        while (pending.TryPop(out var node))
        {
            index[node.Path] = node.Id!.Value;
            foreach (var child in node.Children)
                pending.Push(child);
        }

        return index;
    }
}
