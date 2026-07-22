using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Properties;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Drift guard between find and replace: find enumerates the catalog's searchable descriptors
/// over the snapshot, so every searchable site on a snapshot object must also be rewritten by
/// <see cref="TomModelMutator.ReplaceText"/> in the same scope, at the same path and property.
/// A site find can show but replace cannot rewrite makes the dry-run preview lie. If this test
/// fails after adding a catalog descriptor, extend the walk in TomTextReplacer (or the
/// summarizer, when the value is missing from the snapshot) with the new property.
/// </summary>
public sealed class CatalogSearchableAgreementTests
{
    private const string Sentinel = "drift";

    [Theory]
    [InlineData("names")]
    [InlineData("expressions")]
    [InlineData("descriptions")]
    [InlineData("displayfolders")]
    [InlineData("formatstrings")]
    [InlineData("annotations")]
    [InlineData("all")]
    public void EverySearchableSnapshotSite_IsCoveredByReplace(string scope)
    {
        var db = KitchenSink();
        var snapshot = TomModelSummarizer.Snapshot(db, "kitchen");
        var findSites = SearchableSites(snapshot, scope)
            .Where(s => s.Value.Contains(Sentinel, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(findSites);

        var previews = new TomModelMutator(db).ReplaceText(new ModelReplaceRequest(
                Pattern: Sentinel,
                Replacement: "lifted",
                Scope: scope,
                Regex: false,
                CaseSensitive: false,
                Apply: false))
            .Previews;

        var uncovered = findSites
            .Where(site => !previews.Any(p => p.ObjectPath == site.Path && p.Property == site.Property))
            .Select(site => $"{site.Path} :: {site.Property}")
            .ToList();

        Assert.True(uncovered.Count == 0,
            $"find (scope '{scope}') surfaces sites that replace does not rewrite:\n  " +
            string.Join("\n  ", uncovered) +
            "\nExtend the walk in TomTextReplacer to cover them.");
    }

    /// <summary>Mirrors FindModelHandler's field enumeration: catalog searchable descriptors per
    /// kind, annotations explicit-only.</summary>
    private static IEnumerable<(string Path, string Property, string Value)> SearchableSites(
        ModelSnapshot snapshot, string scope)
    {
        foreach (var obj in Flatten(snapshot.Objects))
        {
            foreach (var descriptor in ModelPropertyCatalog.For(obj.Kind))
            {
                if (descriptor is not { Searchable: true, SearchScope: { } descriptorScope })
                    continue;
                if (scope is not "all" && scope != descriptorScope)
                    continue;
                if (descriptor.Value(obj) is string value && !string.IsNullOrEmpty(value))
                    yield return (obj.Path, descriptor.Header, value);
            }

            if (scope is "annotations" && obj.Properties is not null)
            {
                foreach (var (key, value) in obj.Properties)
                {
                    if (key.StartsWith(PropertyBagKeys.AnnotationPrefix, StringComparison.Ordinal))
                        yield return (obj.Path, key, value);
                }
            }
        }
    }

    private static IEnumerable<ModelObject> Flatten(IEnumerable<ModelObject> objects)
    {
        foreach (var obj in objects)
        {
            yield return obj;
            foreach (var child in Flatten(obj.Children))
                yield return child;
        }
    }

    /// <summary>A model with the sentinel in every text site the snapshot can surface.</summary>
    internal static Database KitchenSink()
    {
        var db = new Database
        {
            Name = "kitchen",
            // 1702+ so the fixture can also carry DAX user-defined functions.
            CompatibilityLevel = 1702,
            Model = new Model { Name = "Model", Description = "drift model description" }
        };
        db.Model.Annotations.Add(new Annotation { Name = "ModelTag", Value = "drift model annotation" });

        var sales = new Table
        {
            Name = "driftSales",
            Description = "drift table description",
            DefaultDetailRowsDefinition = new DetailRowsDefinition { Expression = "SELECTCOLUMNS(driftSales)" },
            RefreshPolicy = new BasicRefreshPolicy
            {
                SourceExpression = "let Source = driftSource in Source",
                PollingExpression = "let Poll = driftPoll in Poll"
            }
        };
        sales.Annotations.Add(new Annotation { Name = "TableTag", Value = "drift table annotation" });

        var amount = new DataColumn
        {
            Name = "driftAmount",
            DataType = DataType.Decimal,
            SourceColumn = "Amount",
            Description = "drift column description",
            DisplayFolder = "driftFolder",
            FormatString = "0.0 \"drift\""
        };
        amount.Annotations.Add(new Annotation { Name = "ColTag", Value = "drift column annotation" });
        sales.Columns.Add(amount);
        sales.Columns.Add(new CalculatedColumn
        {
            Name = "driftCalc",
            DataType = DataType.Int64,
            Expression = "COUNTROWS(driftSales)"
        });

        var measure = new Measure
        {
            Name = "driftTotal",
            Expression = "SUM(driftSales[driftAmount])",
            Description = "drift measure description",
            DisplayFolder = "driftFolder",
            FormatString = "#,0 \"drift\"",
            DetailRowsDefinition = new DetailRowsDefinition { Expression = "TOPN(10, driftSales)" },
            FormatStringDefinition = new FormatStringDefinition { Expression = "\"drift-fmt\"" },
            KPI = new KPI
            {
                Description = "drift kpi description",
                TargetExpression = "[driftTotal] * 1.1",
                StatusExpression = "IF([driftTotal] > 0, 1, -1) -- drift",
                TrendExpression = "SIGN([driftTotal]) -- drift",
                TargetFormatString = "0% drift"
            }
        };
        measure.Annotations.Add(new Annotation { Name = "MeasureTag", Value = "drift measure annotation" });
        measure.KPI.Annotations.Add(new Annotation { Name = "KpiTag", Value = "drift kpi annotation" });
        sales.Measures.Add(measure);

        var hierarchy = new Hierarchy
        {
            Name = "driftHierarchy",
            Description = "drift hierarchy description",
            DisplayFolder = "driftFolder"
        };
        hierarchy.Annotations.Add(new Annotation { Name = "HierTag", Value = "drift hierarchy annotation" });
        hierarchy.Levels.Add(new Level
        {
            Name = "driftLevel",
            Ordinal = 0,
            Column = amount,
            Description = "drift level description"
        });
        sales.Hierarchies.Add(hierarchy);

        var partition = new Partition
        {
            Name = "driftPartition",
            Mode = ModeType.Import,
            Description = "drift partition description",
            Source = new MPartitionSource { Expression = "let Source = driftQuery in Source" }
        };
        partition.Annotations.Add(new Annotation { Name = "PartTag", Value = "drift partition annotation" });
        sales.Partitions.Add(partition);
        db.Model.Tables.Add(sales);

        var customers = new Table { Name = "driftCustomers" };
        var customerKey = new DataColumn { Name = "driftKey", DataType = DataType.Int64 };
        customers.Columns.Add(customerKey);
        db.Model.Tables.Add(customers);

        var salesKey = new DataColumn { Name = "driftCustomerKey", DataType = DataType.Int64 };
        sales.Columns.Add(salesKey);
        var relationship = new SingleColumnRelationship
        {
            FromColumn = salesKey,
            ToColumn = customerKey
        };
        relationship.Annotations.Add(new Annotation { Name = "RelTag", Value = "drift relationship annotation" });
        db.Model.Relationships.Add(relationship);

        var calcTable = new Table { Name = "driftCalcTable" };
        calcTable.Partitions.Add(new Partition
        {
            Name = "driftCalcTable",
            Source = new CalculatedPartitionSource { Expression = "FILTER(driftSales, TRUE())" }
        });
        db.Model.Tables.Add(calcTable);

        var calcGroupTable = new Table
        {
            Name = "driftTimeCalcs",
            CalculationGroup = new CalculationGroup
            {
                Precedence = 1,
                NoSelectionExpression = new CalculationGroupExpression { Expression = "SELECTEDMEASURE() -- driftNone" },
                MultipleOrEmptySelectionExpression = new CalculationGroupExpression { Expression = "SELECTEDMEASURE() -- driftMulti" }
            }
        };
        calcGroupTable.Columns.Add(new DataColumn { Name = "driftItemName", DataType = DataType.String });
        calcGroupTable.CalculationGroup.CalculationItems.Add(new CalculationItem
        {
            Name = "driftYtd",
            Expression = "CALCULATE(SELECTEDMEASURE(), DATESYTD(driftDates)) ",
            Description = "drift item description",
            FormatStringDefinition = new FormatStringDefinition { Expression = "\"drift-item-fmt\"" }
        });
        db.Model.Tables.Add(calcGroupTable);

        var role = new ModelRole { Name = "driftRole", Description = "drift role description" };
        role.Annotations.Add(new Annotation { Name = "RoleTag", Value = "drift role annotation" });
        var roleMember = new WindowsModelRoleMember { MemberName = "drift@example.com", MemberID = "S-1-5-21-42" };
        roleMember.Annotations.Add(new Annotation { Name = "MemberTag", Value = "drift member annotation" });
        role.Members.Add(roleMember);
        role.TablePermissions.Add(new TablePermission
        {
            Table = sales,
            FilterExpression = "driftSales[driftAmount] > 0"
        });
        db.Model.Roles.Add(role);

        var perspective = new Perspective { Name = "driftPerspective", Description = "drift perspective description" };
        perspective.Annotations.Add(new Annotation { Name = "PerspTag", Value = "drift perspective annotation" });
        db.Model.Perspectives.Add(perspective);

        var dataSource = new ProviderDataSource
        {
            Name = "driftWarehouse",
            Description = "drift data source description",
            ConnectionString = "Data Source=drift;"
        };
        dataSource.Annotations.Add(new Annotation { Name = "DsTag", Value = "drift data source annotation" });
        db.Model.DataSources.Add(dataSource);

        db.Model.Expressions.Add(new NamedExpression
        {
            Name = "driftParameter",
            Kind = ExpressionKind.M,
            Expression = "\"driftValue\" meta [IsParameterQuery=true]",
            Description = "drift shared expression description"
        });

        return db;
    }
}
