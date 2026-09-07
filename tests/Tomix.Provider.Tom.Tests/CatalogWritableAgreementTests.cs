using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Properties;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Drift guards between <see cref="ModelPropertyCatalog"/> and the mutator: every property the
/// catalog advertises as writable (surfaced in set/add error hints) must actually be accepted by
/// <see cref="TomModelMutator"/>, and every catalog search scope must be a valid replace scope.
/// If a setter is added or removed in the mutator, update the catalog's Writable flags with it.
/// </summary>
public sealed class CatalogWritableAgreementTests
{
    private static readonly IReadOnlyDictionary<string, string> ValidValues =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = "Renamed",
            ["description"] = "described",
            ["isHidden"] = "true",
            ["dataCategory"] = "Time",
            ["expression"] = "2",
            ["formatString"] = "#,0",
            ["displayFolder"] = "Folder",
            ["sourceColumn"] = "src",
            ["dataType"] = "String",
            ["sortByColumn"] = "C2",
            ["summarizeBy"] = "Sum",
            ["lineageTag"] = "tag-1",
            ["sourceLineageTag"] = "src-tag-1",
            ["isKey"] = "true",
            ["isNullable"] = "false",
            ["isUnique"] = "true",
            ["isAvailableInMDX"] = "false",
            ["keepUniqueRows"] = "true",
            ["encodingHint"] = "Value",
            ["alignment"] = "Left",
            ["tableDetailPosition"] = "1",
            ["isDefaultLabel"] = "true",
            ["isDefaultImage"] = "false",
            ["displayOrdinal"] = "2",
            ["sourceProviderType"] = "int",
            ["isDataTypeInferred"] = "false",
            ["kind"] = "M",
            ["remoteParameterName"] = "RemoteParam",
            ["targetExpression"] = "100",
            ["statusExpression"] = "1",
            ["trendExpression"] = "2",
            ["targetFormatString"] = "#,0",
            ["filterExpression"] = "TRUE()",
            ["isPrivate"] = "true",
            ["excludeFromModelRefresh"] = "true",
            ["excludeFromAutomaticAggregations"] = "true",
            ["alternateSourcePrecedence"] = "1",
            ["showAsVariationsOnly"] = "true",
            ["systemManaged"] = "true",
            ["directLakeIndexingBehavior"] = "Default",
            ["isSimpleMeasure"] = "true",
            ["statusGraphic"] = "Cylinder",
            ["trendGraphic"] = "Standard Arrow",
            ["statusDescription"] = "status",
            ["targetDescription"] = "target",
            ["trendDescription"] = "trend",
            ["hideMembers"] = "HideBlankMembers",
            ["ordinal"] = "1",
            ["mode"] = "Dual",
            ["dataView"] = "Full",
            ["retainDataTillForceCalculate"] = "true",
            ["queryGroup"] = "QG",
            ["isActive"] = "false",
            ["crossFilteringBehavior"] = "BothDirections",
            ["fromCardinality"] = "Many",
            ["toCardinality"] = "One",
            ["securityFilteringBehavior"] = "BothDirections",
            ["relyOnReferentialIntegrity"] = "true",
            ["joinOnDateBehavior"] = "DatePartOnly",
            ["modelPermission"] = "Administrator",
            ["metadataPermission"] = "None",
            ["memberId"] = "aad-object-id",
            ["identityProvider"] = "AzureAD",
            ["memberType"] = "Group",
            ["compatibilityLevel"] = "1704",
            ["culture"] = "en-US",
            ["collation"] = "Latin1_General_BIN",
            ["discourageImplicitMeasures"] = "true",
            ["discourageCompositeModels"] = "true",
            ["defaultMode"] = "Dual",
            ["defaultDataView"] = "Full",
            ["maxParallelismPerQuery"] = "2",
            ["maxParallelismPerRefresh"] = "1",
            ["sourceQueryCulture"] = "en-US",
            ["forceUniqueNames"] = "true",
            ["precedence"] = "1",
            ["maxConnections"] = "5",
            ["impersonationMode"] = "ImpersonateCurrentUser",
            ["isolation"] = "Snapshot",
            ["timeout"] = "30",
            ["contextExpression"] = "true"
        };

    public static TheoryData<ModelObjectKind> CatalogedKinds
        => new(ModelObjectKind.Table, ModelObjectKind.Measure, ModelObjectKind.Column,
            ModelObjectKind.Hierarchy, ModelObjectKind.Level, ModelObjectKind.Partition,
            ModelObjectKind.Relationship, ModelObjectKind.Role, ModelObjectKind.RoleMember,
            ModelObjectKind.Expression, ModelObjectKind.Function,
            ModelObjectKind.CalculationItem, ModelObjectKind.DataSource, ModelObjectKind.Model,
            ModelObjectKind.Kpi, ModelObjectKind.TablePermission);

    [Theory]
    [MemberData(nameof(CatalogedKinds))]
    public void EveryCatalogWritableProperty_IsAcceptedByTheMutator(ModelObjectKind kind)
    {
        var tokens = ModelPropertyCatalog.WritableTokens(kind);
        Assert.NotEmpty(tokens);

        foreach (var token in tokens)
        {
            // A new writable token needs a sample value here, or this guard cannot exercise it.
            // Fail naming the token rather than letting the indexer throw a bare
            // KeyNotFoundException, which says nothing about what drifted.
            Assert.True(
                ValidValues.TryGetValue(token, out var value),
                $"The catalog marks '{token}' writable on {kind}, but {nameof(ValidValues)} has no "
                    + $"sample value for it. Add a '{token}' row so this guard can set it.");

            // Fresh model per property so a 'name' assignment cannot invalidate later paths.
            var db = NewFixture();
            var mutator = new TomModelMutator(db);
            var (path, type) = TargetFor(kind, token);

            var exception = Record.Exception(() => mutator.SetProperty(new ModelObjectSetRequest(
                path, [new ModelPropertyAssignment(token, value)], type)));

            Assert.True(exception is null,
                $"Catalog marks '{token}' writable on {kind}, but the mutator rejected it: {exception?.Message}");
        }
    }

    [Theory]
    [MemberData(nameof(CatalogedKinds))]
    public void UnsupportedPropertyError_ListsTheWritableTokensAsHint(ModelObjectKind kind)
    {
        var db = NewFixture();
        var mutator = new TomModelMutator(db);
        var (path, type) = TargetFor(kind);

        // Not a real token on any kind: statusGraphic used to play this role but became a
        // writable KPI property, so the bogus name has to stay ahead of the catalog.
        var exception = Assert.Throws<NotSupportedException>(() => mutator.SetProperty(new ModelObjectSetRequest(
            path, [new ModelPropertyAssignment("bogusGraphic", "x")], type)));

        // The hint is the only user-visible payload of WritableTokens: every kind with tokens
        // must surface them when a property is rejected, not just the kinds that remember to.
        Assert.Contains("Writable properties:", exception.Message);
        Assert.Contains(ModelPropertyCatalog.WritableTokens(kind)[0], exception.Message);
    }

    [Fact]
    public void EveryCatalogSearchScope_IsAValidReplaceScope()
    {
        foreach (var scope in ModelPropertyCatalog.SearchScopes)
        {
            var mutator = new TomModelMutator(NewFixture());

            // An unknown scope throws ArgumentException before any operation is built.
            var exception = Record.Exception(() => mutator.ReplaceText(new ModelReplaceRequest(
                Pattern: "nothing-matches-this",
                Replacement: "x",
                Scope: scope,
                Regex: false,
                CaseSensitive: false,
                Apply: false)));

            Assert.True(exception is null,
                $"Catalog search scope '{scope}' is not accepted by replace: {exception?.Message}");
        }
    }

    [Fact]
    public void UnsupportedPartitionPropertyHint_OmitsExpression_ForNonMSources()
    {
        // 'expression' is only settable on M-source partitions, so the hint must not
        // advertise it for calculated/entity/policy-range partitions.
        var db = NewFixture();
        db.Model.Tables["T"].Partitions["T"].Source = new CalculatedPartitionSource { Expression = "T2" };
        var mutator = new TomModelMutator(db);

        var exception = Assert.Throws<NotSupportedException>(() => mutator.SetProperty(new ModelObjectSetRequest(
            "T/T", [new ModelPropertyAssignment("bogus", "x")], ModelObjectKind.Partition)));

        Assert.DoesNotContain("expression", exception.Message);
        Assert.Contains("name", exception.Message);
    }

    [Fact]
    public void UnsupportedPartitionPropertyHint_IncludesExpression_ForMSources()
    {
        var mutator = new TomModelMutator(NewFixture());

        var exception = Assert.Throws<NotSupportedException>(() => mutator.SetProperty(new ModelObjectSetRequest(
            "T/T", [new ModelPropertyAssignment("bogus", "x")], ModelObjectKind.Partition)));

        Assert.Contains("expression", exception.Message);
    }

    private static (string Path, ModelObjectKind? Type) TargetFor(ModelObjectKind kind, string? token = null)
    {
        // Source-bound partition properties live on different partitions: 'expression' is
        // exercised on the M-source partition, 'retainDataTillForceCalculate' on the calculated one.
        if (kind == ModelObjectKind.Partition && token == "retainDataTillForceCalculate")
            return ("T/Calc", ModelObjectKind.Partition);

        // Precedence is a calculation-group table property; plain tables reject it.
        if (kind == ModelObjectKind.Table && token == "precedence")
            return ("tables/CG", ModelObjectKind.Table);

        // Data-source fields are provider-only or structured-only, so the harness splits them
        // across one data source of each kind.
        if (kind == ModelObjectKind.DataSource)
            return token == "contextExpression"
                ? ("DataSources/SDS", ModelObjectKind.DataSource)
                : ("DataSources/PDS", ModelObjectKind.DataSource);

        return kind switch
        {
            ModelObjectKind.Table => ("tables/T", null),
            ModelObjectKind.Measure => ("T/M", ModelObjectKind.Measure),
            ModelObjectKind.Column => ("T/C", ModelObjectKind.Column),
            ModelObjectKind.Hierarchy => ("T/H", ModelObjectKind.Hierarchy),
            ModelObjectKind.Level => ("T/H/L", ModelObjectKind.Level),
            ModelObjectKind.Partition => ("T/T", ModelObjectKind.Partition),
            // Endpoint addressing, the canonical way to target a relationship.
            ModelObjectKind.Relationship => ("T[D]->T2[D]", ModelObjectKind.Relationship),
            ModelObjectKind.Role => ("Readers", ModelObjectKind.Role),
            ModelObjectKind.RoleMember => ("Readers/user@contoso.com", ModelObjectKind.RoleMember),
            ModelObjectKind.Expression => ("Expressions/E", null),
            ModelObjectKind.Function => ("Functions/F", null),
            // The model root is not a snapshot object; "." addresses it for every token.
            ModelObjectKind.Model => (".", null),
            ModelObjectKind.CalculationItem => ("CG/CI", ModelObjectKind.CalculationItem),
            ModelObjectKind.Kpi => ("T/M", ModelObjectKind.Kpi),
            ModelObjectKind.TablePermission => ("Readers/T", null),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static Database NewFixture()
    {
        // 1702+ so the fixture can carry DAX user-defined functions.
        var db = NewDatabase(compatibilityLevel: 1702);
        var table = new Table { Name = "T" };
        table.Partitions.Add(new Partition
        {
            Name = "T",
            Source = new MPartitionSource { Expression = "let x = 1 in x" }
        });
        table.Partitions.Add(new Partition
        {
            Name = "Calc",
            Source = new CalculatedPartitionSource { Expression = "T2" }
        });
        table.Columns.Add(new DataColumn { Name = "C", DataType = DataType.Int64 });
        table.Columns.Add(new DataColumn { Name = "C2", DataType = DataType.String });
        table.Columns.Add(new DataColumn { Name = "D", DataType = DataType.DateTime });
        table.Measures.Add(new Measure { Name = "M", Expression = "1" });
        var hierarchy = new Hierarchy { Name = "H" };
        hierarchy.Levels.Add(new Level { Name = "L", Column = table.Columns["C"] });
        table.Hierarchies.Add(hierarchy);
        db.Model.Tables.Add(table);
        // A second table so a relationship can join across the two; date-time columns because
        // joinOnDateBehavior is only meaningful for datetime joins.
        var table2 = new Table { Name = "T2" };
        table2.Partitions.Add(new Partition
        {
            Name = "T2",
            Source = new MPartitionSource { Expression = "let x = 2 in x" }
        });
        table2.Columns.Add(new DataColumn { Name = "D", DataType = DataType.DateTime });
        db.Model.Tables.Add(table2);
        db.Model.Relationships.Add(new SingleColumnRelationship
        {
            FromColumn = table.Columns["D"],
            ToColumn = table2.Columns["D"],
            FromCardinality = RelationshipEndCardinality.Many,
            ToCardinality = RelationshipEndCardinality.One
        });
        // KPI on the measure so Kpi-kind set paths resolve; role + permission so
        // TablePermission set paths resolve, and an external member so RoleMember paths do.
        table.Measures["M"].KPI = new KPI { TargetExpression = "0", StatusExpression = "0" };
        var role = new ModelRole { Name = "Readers" };
        role.Members.Add(new ExternalModelRoleMember { MemberName = "user@contoso.com", IdentityProvider = "AzureAD" });
        role.TablePermissions.Add(new TablePermission { Name = "T", Table = table, FilterExpression = "TRUE()" });
        db.Model.Roles.Add(role);
        db.Model.Expressions.Add(new NamedExpression
        {
            Name = "E",
            Kind = ExpressionKind.M,
            Expression = "\"v\" meta [IsParameterQuery=true]"
        });
        db.Model.Functions.Add(new Function { Name = "F", Expression = "(x) => x" });
        // A calculation-group table so precedence and CalculationItem set paths resolve.
        var calcGroupTable = new Table { Name = "CG" };
        calcGroupTable.CalculationGroup = new CalculationGroup { Precedence = 1 };
        calcGroupTable.CalculationGroup.CalculationItems.Add(
            new CalculationItem { Name = "CI", Expression = "[M] * 2" });
        db.Model.Tables.Add(calcGroupTable);
        // One data source of each kind: provider fields (impersonation, isolation, timeout)
        // resolve on PDS, the structured ContextExpression on SDS.
        db.Model.DataSources.Add(new ProviderDataSource
        {
            Name = "PDS",
            Provider = "SQLNCLI11",
            ConnectionString = "Data Source=sql01"
        });
        db.Model.DataSources.Add(new StructuredDataSource { Name = "SDS" });
        // A query group so Partition set paths can resolve 'queryGroup' by name. The name is
        // derived from the folder path — TOM refuses to set Name directly.
        db.Model.QueryGroups.Add(new QueryGroup { Folder = "QG" });
        return db;
    }
}
