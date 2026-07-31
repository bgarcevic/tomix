using System.Text.Json.Nodes;
using Tomix.Core.Models;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// The merge builder is the behavior contract for granular deployment: it decides which
/// target-owned objects survive a createOrReplace. Tests operate on raw TMSL JSON so every
/// option path is covered without a live server.
/// </summary>
public sealed class TmslDeployScriptBuilderTests
{
    private const string DeployName = "Sales";

    // -- Script envelope ---------------------------------------------------------------------

    [Fact]
    public void AddressesExistingDatabaseByName_AndPinsTargetId()
    {
        var script = Build(SourceDb(), TargetDb(), targetId: "guid-from-target");

        Assert.Equal(DeployName, script["createOrReplace"]!["object"]!["database"]!.GetValue<string>());
        Assert.Equal("guid-from-target", Db(script)["id"]!.GetValue<string>());
        Assert.Equal(DeployName, Db(script)["name"]!.GetValue<string>());
    }

    [Fact]
    public void NewDatabase_UsesDeployNameAsId()
    {
        var script = Build(SourceDb(), target: null, targetId: null);

        Assert.Equal(DeployName, Db(script)["id"]!.GetValue<string>());
    }

    // -- Roles -------------------------------------------------------------------------------

    [Fact]
    public void PreserveRoles_TargetRolesWinEntirely()
    {
        var source = SourceDb(roles: Roles(("Reader", "source@x.com"), ("SourceOnly", "dev@x.com")));
        var target = TargetDb(roles: Roles(("Reader", "prod-group@x.com"), ("ProdOnly", "admin@x.com")));

        var model = Model(Build(source, target));

        var roleNames = model["roles"]!.AsArray().Select(r => r!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["Reader", "ProdOnly"], roleNames);
        Assert.Equal("prod-group@x.com", FirstMemberName(model, "Reader"));
    }

    [Fact]
    public void PreserveRoles_TargetHasNone_RolesRemoved()
    {
        var source = SourceDb(roles: Roles(("Reader", "source@x.com")));
        var model = Model(Build(source, TargetDb()));

        Assert.False(model.ContainsKey("roles"));
    }

    [Fact]
    public void DeployRoles_PreserveMembers_SourceDefinitionsWithTargetMembers()
    {
        var source = SourceDb(roles: Roles(("Reader", "source@x.com"), ("NewRole", "dev@x.com")));
        var target = TargetDb(roles: Roles(("Reader", "prod-group@x.com")));

        var model = Model(Build(source, target, options: new ModelDeployOptions(DeployRoles: true)));

        var roleNames = model["roles"]!.AsArray().Select(r => r!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["Reader", "NewRole"], roleNames);
        Assert.Equal("prod-group@x.com", FirstMemberName(model, "Reader"));
        Assert.Empty(Role(model, "NewRole")["members"]!.AsArray());
    }

    [Fact]
    public void DeployRolesAndMembers_SourceWins()
    {
        var source = SourceDb(roles: Roles(("Reader", "source@x.com")));
        var target = TargetDb(roles: Roles(("Reader", "prod-group@x.com")));

        var model = Model(Build(source, target, options: new ModelDeployOptions(DeployRoles: true, DeployRoleMembers: true)));

        Assert.Equal("source@x.com", FirstMemberName(model, "Reader"));
    }

    [Fact]
    public void CloudTarget_StripsRoleMemberIds()
    {
        var source = SourceDb(roles: Roles(("Reader", "source@x.com")));
        AddMemberId(source, "Reader", "stale-guid");

        var model = Model(Build(source, target: null, targetId: null,
            new ModelDeployOptions(DeployRoles: true, DeployRoleMembers: true),
            stripRoleMemberIds: true));

        var member = Role(model, "Reader")["members"]!.AsArray()[0]!.AsObject();
        Assert.False(member.ContainsKey("memberId"));
        Assert.Equal("source@x.com", member["memberName"]!.GetValue<string>());
    }

    /// <summary>
    /// The common cloud case: roles are preserved (the default), so the members in the script
    /// come from the target and carry service-assigned ids. Stripping must therefore happen
    /// after the merge — this pins that ordering, which the source-side test cannot.
    /// </summary>
    [Fact]
    public void CloudTarget_StripsRoleMemberIds_OnPreservedTargetMembers()
    {
        var source = SourceDb(roles: Roles(("Reader", "source@x.com")));
        var target = TargetDb(roles: Roles(("Reader", "prod-group@x.com")));
        AddMemberId(target, "Reader", "service-assigned-guid");

        var model = Model(Build(source, target, stripRoleMemberIds: true));

        var member = Role(model, "Reader")["members"]!.AsArray()[0]!.AsObject();
        Assert.False(member.ContainsKey("memberId"));
        Assert.Equal("prod-group@x.com", member["memberName"]!.GetValue<string>());
    }

    // -- Data sources ------------------------------------------------------------------------

    [Fact]
    public void PreserveConnections_TargetWinsPerName_SourceOnlyKept_TargetOnlyAdded()
    {
        var source = SourceDb(dataSources: DataSources(("Warehouse", "Server=dev"), ("NewSource", "Server=new")));
        var target = TargetDb(dataSources: DataSources(("Warehouse", "Server=prod;Password=secret"), ("LegacySource", "Server=legacy")));

        var model = Model(Build(source, target));

        var byName = model["dataSources"]!.AsArray()
            .ToDictionary(d => d!["name"]!.GetValue<string>(), d => d!["connectionString"]!.GetValue<string>());
        Assert.Equal("Server=prod;Password=secret", byName["Warehouse"]);
        Assert.Equal("Server=new", byName["NewSource"]);
        Assert.Equal("Server=legacy", byName["LegacySource"]);
    }

    [Fact]
    public void DeployConnections_SourceWins()
    {
        var source = SourceDb(dataSources: DataSources(("Warehouse", "Server=dev")));
        var target = TargetDb(dataSources: DataSources(("Warehouse", "Server=prod")));

        var model = Model(Build(source, target, options: new ModelDeployOptions(DeployConnections: true)));

        Assert.Equal("Server=dev", model["dataSources"]!.AsArray()[0]!["connectionString"]!.GetValue<string>());
    }

    /// <summary>
    /// Analysis Services object names are case-insensitive, so a casing difference between
    /// source and target is the same object. Matching case-sensitively here would not just skip
    /// the merge — it would emit both entries and produce a duplicate-name TMSL the engine rejects.
    /// </summary>
    [Fact]
    public void PreserveConnections_MatchesNameIgnoringCase_WithoutDuplicating()
    {
        var source = SourceDb(dataSources: DataSources(("warehouse", "Server=dev")));
        var target = TargetDb(dataSources: DataSources(("Warehouse", "Server=prod")));

        var model = Model(Build(source, target));

        var dataSource = Assert.Single(model["dataSources"]!.AsArray())!.AsObject();
        Assert.Equal("Server=prod", dataSource["connectionString"]!.GetValue<string>());
    }

    // -- Shared expressions ------------------------------------------------------------------

    [Fact]
    public void PreserveExpressions_TargetValueWins_SourceOnlyStillDeploys()
    {
        var source = SourceDb(expressions: Expressions(("Environment", "\"dev\""), ("NewParam", "\"n\"")));
        var target = TargetDb(expressions: Expressions(("Environment", "\"prod\""), ("LegacyParam", "\"l\"")));

        var model = Model(Build(source, target));

        var byName = model["expressions"]!.AsArray()
            .ToDictionary(e => e!["name"]!.GetValue<string>(), e => e!["expression"]!.GetValue<string>());
        Assert.Equal("\"prod\"", byName["Environment"]);
        Assert.Equal("\"n\"", byName["NewParam"]);
        Assert.Equal("\"l\"", byName["LegacyParam"]);
    }

    [Fact]
    public void PreserveExpressions_SkippedBelowCompat1400()
    {
        var source = SourceDb(expressions: Expressions(("Environment", "\"dev\"")));
        source["compatibilityLevel"] = 1200;
        var target = TargetDb(expressions: Expressions(("Environment", "\"prod\"")));

        var model = Model(Build(source, target));

        Assert.Equal("\"dev\"", model["expressions"]!.AsArray()[0]!["expression"]!.GetValue<string>());
    }

    /// <summary>
    /// A database without an explicit compatibilityLevel must still preserve expressions: the
    /// assumed default is 1400+ (shared expressions supported), because guessing "too old" would
    /// silently overwrite environment-specific M parameters.
    /// </summary>
    [Fact]
    public void PreserveExpressions_MissingCompatibilityLevel_StillPreserves()
    {
        var source = SourceDb(expressions: Expressions(("Environment", "\"dev\"")));
        source.Remove("compatibilityLevel");
        var target = TargetDb(expressions: Expressions(("Environment", "\"prod\"")));

        var model = Model(Build(source, target));

        Assert.Equal("\"prod\"", model["expressions"]!.AsArray()[0]!["expression"]!.GetValue<string>());
    }

    // -- Partitions --------------------------------------------------------------------------

    [Fact]
    public void PreservePartitions_TargetPartitionsWin()
    {
        var source = SourceDb(tables: new JsonArray(QueryTable("Fact", "source-partition")));
        var target = TargetDb(tables: new JsonArray(QueryTable("Fact", "prod-2023", "prod-2024")));

        var model = Model(Build(source, target));

        Assert.Equal(["prod-2023", "prod-2024"], PartitionNames(model, "Fact"));
    }

    [Fact]
    public void DeployPartitions_PolicyTablePreserved_PlainTableOverwritten()
    {
        var source = SourceDb(tables: new JsonArray(
            QueryTable("Fact", "source-template"),
            QueryTable("Dim", "source-dim")));
        var target = TargetDb(tables: new JsonArray(
            WithPolicy(QueryTable("Fact", "2023Q1", "2023Q2", "2024")),
            QueryTable("Dim", "prod-dim")));

        var model = Model(Build(source, target, options: new ModelDeployOptions(DeployPartitions: true, DeployPolicyPartitions: false)));

        // The incremental-refresh table keeps its processed partitions AND the policy that
        // generated them; the plain table takes the source's partitions.
        Assert.Equal(["2023Q1", "2023Q2", "2024"], PartitionNames(model, "Fact"));
        Assert.True(Table(model, "Fact").ContainsKey("refreshPolicy"));
        Assert.Equal(["source-dim"], PartitionNames(model, "Dim"));
    }

    [Fact]
    public void DeployPolicyPartitions_SourceWinsEverywhere()
    {
        var source = SourceDb(tables: new JsonArray(WithPolicy(QueryTable("Fact", "source-template"))));
        var target = TargetDb(tables: new JsonArray(WithPolicy(QueryTable("Fact", "2023Q1", "2024"))));

        var model = Model(Build(source, target, options: new ModelDeployOptions(DeployPartitions: true, DeployPolicyPartitions: true)));

        Assert.Equal(["source-template"], PartitionNames(model, "Fact"));
    }

    /// <summary>
    /// Policy-partition protection keys off the policy's sourceExpression, since that is what
    /// generates the dated partitions holding processed data. A refreshPolicy without one cannot
    /// have generated any, so it gets no special treatment and --deploy-partitions applies.
    /// </summary>
    [Fact]
    public void DeployPartitions_PolicyWithoutSourceExpression_PartitionsOverwritten()
    {
        var source = SourceDb(tables: new JsonArray(QueryTable("Fact", "source-template")));
        var target = TargetDb(tables: new JsonArray(
            WithPolicy(QueryTable("Fact", "2023Q1"), sourceExpression: null)));

        var model = Model(Build(source, target, options: new ModelDeployOptions(DeployPartitions: true)));

        Assert.Equal(["source-template"], PartitionNames(model, "Fact"));
    }

    /// <summary>Table names are case-insensitive in Analysis Services; a casing difference
    /// between source and target must not silently skip partition preservation.</summary>
    [Fact]
    public void PreservePartitions_MatchesTableNameIgnoringCase()
    {
        var source = SourceDb(tables: new JsonArray(QueryTable("fact", "source-partition")));
        var target = TargetDb(tables: new JsonArray(QueryTable("Fact", "prod-2024")));

        var model = Model(Build(source, target));

        Assert.Equal(["prod-2024"], PartitionNames(model, "fact"));
    }

    [Fact]
    public void CalculatedTables_NeverPreserved()
    {
        var source = SourceDb(tables: new JsonArray(CalculatedTable("Calendar", "source-calc")));
        var target = TargetDb(tables: new JsonArray(CalculatedTable("Calendar", "prod-calc")));

        var model = Model(Build(source, target));

        Assert.Equal(["source-calc"], PartitionNames(model, "Calendar"));
    }

    [Fact]
    public void TableMissingOnTarget_KeepsSourcePartitions()
    {
        var source = SourceDb(tables: new JsonArray(QueryTable("Brand", "source-brand")));
        var target = TargetDb(tables: new JsonArray(QueryTable("Other", "prod-other")));

        var model = Model(Build(source, target));

        Assert.Equal(["source-brand"], PartitionNames(model, "Brand"));
    }

    // -- Placeholder partitions for policy tables ----------------------------------------------

    [Fact]
    public void PolicyTableWithoutPartitions_GetsPlaceholderPartition()
    {
        var table = WithPolicy(QueryTable("Fact"));
        var source = SourceDb(tables: new JsonArray(table));

        var model = Model(Build(source, target: null, targetId: null,
            new ModelDeployOptions(DeployPartitions: true, DeployPolicyPartitions: true)));

        var partitions = Table(model, "Fact")["partitions"]!.AsArray();
        var partition = Assert.Single(partitions)!.AsObject();
        Assert.Equal("import", partition["mode"]!.GetValue<string>());
        Assert.Equal("m", partition["source"]!["type"]!.GetValue<string>());
        Assert.Equal("let Source = Sql in Source", partition["source"]!["expression"]!.GetValue<string>());
    }

    [Fact]
    public void PolicyTableWithPartitions_NoPlaceholderAdded()
    {
        var source = SourceDb(tables: new JsonArray(WithPolicy(QueryTable("Fact", "existing"))));

        var model = Model(Build(source, target: null, targetId: null));

        Assert.Equal(["existing"], PartitionNames(model, "Fact"));
    }

    // -- Helpers -------------------------------------------------------------------------------

    private static JsonObject Build(
        JsonObject source,
        JsonObject? target,
        string? targetId = "target-id",
        ModelDeployOptions? options = null,
        bool stripRoleMemberIds = false)
    {
        var script = TmslDeployScriptBuilder.Build(
            source.ToJsonString(),
            target?.ToJsonString(),
            DeployName,
            target is null ? targetId : targetId ?? "target-id",
            options ?? ModelDeployOptions.Preserve,
            stripRoleMemberIds);
        return (JsonObject)JsonNode.Parse(script)!;
    }

    private static JsonObject Db(JsonObject script) => script["createOrReplace"]!["database"]!.AsObject();
    private static JsonObject Model(JsonObject script) => Db(script)["model"]!.AsObject();

    private static JsonObject SourceDb(
        JsonArray? tables = null, JsonArray? roles = null, JsonArray? dataSources = null, JsonArray? expressions = null)
        => Database("SourceName", "source-id", tables, roles, dataSources, expressions);

    private static JsonObject TargetDb(
        JsonArray? tables = null, JsonArray? roles = null, JsonArray? dataSources = null, JsonArray? expressions = null)
        => Database(DeployName, "target-id", tables, roles, dataSources, expressions);

    private static JsonObject Database(
        string name, string id, JsonArray? tables, JsonArray? roles, JsonArray? dataSources, JsonArray? expressions)
    {
        var model = new JsonObject();
        if (tables is not null) model["tables"] = tables;
        if (roles is not null) model["roles"] = roles;
        if (dataSources is not null) model["dataSources"] = dataSources;
        if (expressions is not null) model["expressions"] = expressions;

        return new JsonObject
        {
            ["name"] = name,
            ["id"] = id,
            ["compatibilityLevel"] = 1601,
            ["model"] = model
        };
    }

    private static JsonObject QueryTable(string name, params string[] partitionNames)
    {
        var partitions = new JsonArray();
        foreach (var partitionName in partitionNames)
        {
            partitions.Add(new JsonObject
            {
                ["name"] = partitionName,
                ["mode"] = "import",
                ["source"] = new JsonObject { ["type"] = "m", ["expression"] = "let Source = Sql in Source" }
            });
        }

        var table = new JsonObject { ["name"] = name };
        if (partitions.Count > 0)
            table["partitions"] = partitions;
        return table;
    }

    private static JsonObject CalculatedTable(string name, string partitionName)
        => new()
        {
            ["name"] = name,
            ["partitions"] = new JsonArray(new JsonObject
            {
                ["name"] = partitionName,
                ["source"] = new JsonObject { ["type"] = "calculated", ["expression"] = "CALENDARAUTO()" }
            })
        };

    private static JsonObject WithPolicy(
        JsonObject table, string? sourceExpression = "let Source = Sql in Source")
    {
        var policy = new JsonObject
        {
            ["policyType"] = "basic",
            ["rollingWindowGranularity"] = "year",
            ["rollingWindowPeriods"] = 5,
            ["incrementalGranularity"] = "month",
            ["incrementalPeriods"] = 3
        };
        if (sourceExpression is not null)
            policy["sourceExpression"] = sourceExpression;

        table["refreshPolicy"] = policy;
        return table;
    }

    private static JsonArray Roles(params (string Name, string Member)[] roles)
    {
        var array = new JsonArray();
        foreach (var (name, member) in roles)
        {
            array.Add(new JsonObject
            {
                ["name"] = name,
                ["modelPermission"] = "read",
                ["members"] = new JsonArray(new JsonObject { ["memberName"] = member })
            });
        }
        return array;
    }

    private static JsonArray DataSources(params (string Name, string ConnectionString)[] sources)
    {
        var array = new JsonArray();
        foreach (var (name, connectionString) in sources)
            array.Add(new JsonObject { ["name"] = name, ["connectionString"] = connectionString });
        return array;
    }

    private static JsonArray Expressions(params (string Name, string Expression)[] expressions)
    {
        var array = new JsonArray();
        foreach (var (name, expression) in expressions)
            array.Add(new JsonObject { ["name"] = name, ["kind"] = "m", ["expression"] = expression });
        return array;
    }

    private static JsonObject Table(JsonObject model, string name)
        => model["tables"]!.AsArray().OfType<JsonObject>().Single(t => t["name"]!.GetValue<string>() == name);

    private static JsonObject Role(JsonObject model, string name)
        => model["roles"]!.AsArray().OfType<JsonObject>().Single(r => r["name"]!.GetValue<string>() == name);

    private static List<string> PartitionNames(JsonObject model, string tableName)
        => Table(model, tableName)["partitions"]!.AsArray()
            .Select(p => p!["name"]!.GetValue<string>()).ToList();

    private static string FirstMemberName(JsonObject model, string roleName)
        => Role(model, roleName)["members"]!.AsArray()[0]!["memberName"]!.GetValue<string>();

    private static void AddMemberId(JsonObject database, string roleName, string memberId)
    {
        var role = database["model"]!["roles"]!.AsArray().OfType<JsonObject>()
            .Single(r => r["name"]!.GetValue<string>() == roleName);
        role["members"]!.AsArray()[0]!.AsObject()["memberId"] = memberId;
    }
}
