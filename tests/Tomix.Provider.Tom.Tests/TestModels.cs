using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// In-memory TOM fixtures and mutation-request factories for this project's tests.
/// </summary>
/// <remarks>
/// Every test here mutates its own <see cref="Database"/> in place, so each call returns a fresh
/// graph — there is deliberately no cached/shared model. Adding a parameter to
/// <see cref="ModelObjectAddRequest"/> and friends used to mean editing four test files; it now
/// means editing one factory below.
/// </remarks>
internal static class TestModels
{
    /// <summary>A partition source that deserializes but reads nothing: the fixtures never refresh.</summary>
    private const string InertMExpression = "let Source = #table({}, {}) in Source";

    /// <summary>An empty model. Pass 1702 or higher for fixtures carrying DAX user-defined functions.</summary>
    public static Database NewDatabase(string name = "M", int? compatibilityLevel = null)
    {
        var db = new Database { Name = name, Model = new Model { Name = "Model" } };
        if (compatibilityLevel is not null)
            db.CompatibilityLevel = compatibilityLevel.Value;

        return db;
    }

    /// <summary>
    /// A table with one import partition named after it (the Desktop default) and an
    /// <see cref="DataType.Int64"/> column per entry in <paramref name="columns"/>.
    /// </summary>
    public static Table NewTable(string name, params string[] columns)
    {
        var table = new Table { Name = name };
        foreach (var column in columns)
            table.Columns.Add(new DataColumn { Name = column, DataType = DataType.Int64, SourceColumn = column });

        table.Partitions.Add(new Partition
        {
            Name = name,
            Mode = ModeType.Import,
            Source = new MPartitionSource { Expression = InertMExpression }
        });
        return table;
    }

    /// <summary>Adds a partitioned, column-less table to <paramref name="db"/> and returns it.</summary>
    public static Table AddTable(Database db, string name)
    {
        var table = new Table { Name = name };
        table.Partitions.Add(new Partition
        {
            Name = name,
            Source = new MPartitionSource { Expression = "let x = 1 in x" }
        });
        db.Model.Tables.Add(table);
        return table;
    }

    /// <summary>
    /// A model with a single <c>Sales</c> table. <paramref name="withAmountColumn"/> defaults to
    /// <c>false</c> so callers that assert on the column set are not surprised by an extra column.
    /// </summary>
    public static Database WithSales(bool withAmountColumn = false, string databaseName = "M")
    {
        var db = NewDatabase(databaseName);
        var sales = withAmountColumn ? NewTable("Sales", "Amount") : NewTable("Sales");
        db.Model.Tables.Add(sales);
        return db;
    }

    /// <summary>
    /// <c>Sales</c> (CustomerId, Amount, MonthName, MonthNo) many-to-one <c>Customer</c> (Id), one
    /// import partition per table named after the table (the Desktop default).
    /// </summary>
    public static Database WithRelationship()
    {
        var db = NewDatabase();

        var sales = NewTable("Sales", "CustomerId", "Amount", "MonthName", "MonthNo");
        var customer = NewTable("Customer", "Id");
        db.Model.Tables.Add(sales);
        db.Model.Tables.Add(customer);

        db.Model.Relationships.Add(new SingleColumnRelationship
        {
            Name = "SalesToCustomer",
            FromColumn = sales.Columns["CustomerId"],
            ToColumn = customer.Columns["Id"],
            FromCardinality = RelationshipEndCardinality.Many,
            ToCardinality = RelationshipEndCardinality.One
        });

        return db;
    }

    // ---- request factories -------------------------------------------------------------------

    public static ModelObjectAddRequest Add(
        string path, string? type, string? value = null, bool ifNotExists = false)
        => new(path, type, value, Properties: [], IfNotExists: ifNotExists);

    public static ModelObjectRemoveRequest Remove(
        string path, ModelObjectKind? type = null, bool ifExists = false)
        => new(path, type, ifExists);

    public static ModelObjectMoveRequest Move(string path, string newParent, string? newName = null)
        => new(path, Type: null, newParent, newName);

    public static ModelObjectSetRequest Set(
        string path, string property, string value, ModelObjectKind? type = null)
        => new(path, [new ModelPropertyAssignment(property, value)], type);

    public static ModelReplaceRequest Replace(
        string pattern, string replacement, string scope, bool apply = true)
        => new(pattern, replacement, scope, Regex: false, CaseSensitive: false, Apply: apply);
}
