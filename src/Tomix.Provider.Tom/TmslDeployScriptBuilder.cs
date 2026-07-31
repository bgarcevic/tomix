using System.Text.Json;
using System.Text.Json.Nodes;
using Tomix.Core.Models;

namespace Tomix.Provider.Tom;

/// <summary>
/// Builds a <c>createOrReplace</c> TMSL script from a serialized source database, selectively
/// preserving objects owned by the existing target database according to
/// <see cref="ModelDeployOptions"/>. Operates purely on TMSL JSON so preserved target objects
/// (partitions holding processed data, connection strings, role members) round-trip verbatim
/// instead of passing through TOM object mutation.
/// </summary>
internal static class TmslDeployScriptBuilder
{
    /// <param name="sourceDatabaseJson">The source database serialized as TMSL JSON.</param>
    /// <param name="targetDatabaseJson">The existing target database serialized as TMSL JSON
    /// (with restricted information), or null when the target does not exist or nothing is
    /// preserved.</param>
    /// <param name="deployName">Database name to deploy as; addresses the existing target.</param>
    /// <param name="targetId">The existing target's internal ID, preserved so deploys do not
    /// churn the database ID; null for new databases (ID becomes <paramref name="deployName"/>).</param>
    /// <param name="stripRoleMemberIds">Remove service-assigned <c>memberId</c> values, required
    /// for Power BI and Azure AS targets where stale IDs conflict on redeploy.</param>
    public static string Build(
        string sourceDatabaseJson,
        string? targetDatabaseJson,
        string deployName,
        string? targetId,
        ModelDeployOptions options,
        bool stripRoleMemberIds)
    {
        var database = JsonNode.Parse(sourceDatabaseJson) as JsonObject
            ?? throw new InvalidOperationException("Source database did not serialize to a JSON object.");

        database["id"] = targetId ?? deployName;
        database["name"] = deployName;

        if (database["model"] is JsonObject model)
        {
            if (targetDatabaseJson is not null
                && JsonNode.Parse(targetDatabaseJson) is JsonObject target
                && target["model"] is JsonObject targetModel)
            {
                MergePreservedObjects(model, targetModel, CompatibilityLevel(database), options);
            }

            if (stripRoleMemberIds)
                StripRoleMemberIds(model);

            AddPlaceholderPartitionsToPolicyTables(model);
        }

        var script = new JsonObject
        {
            ["createOrReplace"] = new JsonObject
            {
                // TMSL addresses the existing database by NAME, not ID.
                ["object"] = new JsonObject { ["database"] = deployName },
                ["database"] = database
            }
        };

        return script.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void MergePreservedObjects(
        JsonObject model, JsonObject targetModel, int compatibilityLevel, ModelDeployOptions options)
    {
        if (!options.DeployRoles)
            PreserveRoles(model, targetModel);
        else if (!options.DeployRoleMembers)
            PreserveRoleMembers(model, targetModel);

        if (!options.DeployConnections)
            PreserveDataSources(model, targetModel);

        if (!options.DeployPartitions || !options.DeployPolicyPartitions)
            PreservePartitions(model, targetModel, options);

        if (compatibilityLevel >= 1400 && !options.DeploySharedExpressions)
            PreserveSharedExpressions(model, targetModel);
    }

    /// <summary>The target's roles win entirely: not deploying roles must not add, remove, or
    /// alter any security definition on the target.</summary>
    private static void PreserveRoles(JsonObject model, JsonObject targetModel)
    {
        if (targetModel["roles"] is JsonArray targetRoles && targetRoles.Count > 0)
            model["roles"] = targetRoles.DeepClone();
        else
            model.Remove("roles");
    }

    /// <summary>Source role definitions deploy, but membership stays as configured on the
    /// target (matched by role name). Roles new in the source start with no members.</summary>
    private static void PreserveRoleMembers(JsonObject model, JsonObject targetModel)
    {
        if (model["roles"] is not JsonArray roles)
            return;

        foreach (var role in roles.OfType<JsonObject>())
        {
            var targetRole = FindByName(targetModel["roles"] as JsonArray, Name(role));
            role["members"] = targetRole?["members"] is JsonArray targetMembers
                ? targetMembers.DeepClone()
                : new JsonArray();
        }
    }

    /// <summary>Per-name merge: the target's data sources (including credential-bearing
    /// connection strings) win over same-named source entries and target-only entries are kept,
    /// since preserved partitions may reference them. Source-only entries still deploy.</summary>
    private static void PreserveDataSources(JsonObject model, JsonObject targetModel)
    {
        if (targetModel["dataSources"] is not JsonArray targetSources || targetSources.Count == 0)
            return;

        if (model["dataSources"] is not JsonArray sources)
        {
            sources = new JsonArray();
            model["dataSources"] = sources;
        }

        foreach (var targetSource in targetSources.OfType<JsonObject>())
        {
            RemoveByName(sources, Name(targetSource));
            sources.Add(targetSource.DeepClone());
        }
    }

    /// <summary>Per-name merge mirroring <see cref="PreserveDataSources"/>: target expression
    /// values (environment-specific M parameters) win, target-only expressions are kept because
    /// preserved partitions may reference them, and source-only expressions still deploy so new
    /// parameters do not break the model they are referenced from.</summary>
    private static void PreserveSharedExpressions(JsonObject model, JsonObject targetModel)
    {
        if (targetModel["expressions"] is not JsonArray targetExpressions || targetExpressions.Count == 0)
            return;

        if (model["expressions"] is not JsonArray expressions)
        {
            expressions = new JsonArray();
            model["expressions"] = expressions;
        }

        foreach (var targetExpression in targetExpressions.OfType<JsonObject>())
        {
            RemoveByName(expressions, Name(targetExpression));
            expressions.Add(targetExpression.DeepClone());
        }
    }

    /// <summary>
    /// Per-table partition preservation. A table keeps the target's partitions (and refresh
    /// policy) when partitions are not deployed at all, or when the target table carries a
    /// refresh policy with a source expression and policy partitions are not deployed — the
    /// latter protects processed incremental-refresh data from being wiped by a metadata deploy.
    /// Only applies when both sides are query tables; calculated tables always take the source.
    /// </summary>
    private static void PreservePartitions(JsonObject model, JsonObject targetModel, ModelDeployOptions options)
    {
        if (model["tables"] is not JsonArray tables)
            return;

        foreach (var table in tables.OfType<JsonObject>())
        {
            if (FindByName(targetModel["tables"] as JsonArray, Name(table)) is not JsonObject targetTable)
                continue;

            if (!IsQueryTable(table) || !IsQueryTable(targetTable))
                continue;

            var preserve = !options.DeployPartitions
                || (!options.DeployPolicyPartitions && HasPolicyWithSourceExpression(targetTable));

            if (!preserve)
                continue;

            table["partitions"] = targetTable["partitions"] is JsonArray targetPartitions
                ? targetPartitions.DeepClone()
                : new JsonArray();

            // Keep policy and partitions consistent: preserved policy-generated partitions must
            // pair with the policy that generated them.
            if (targetTable["refreshPolicy"] is JsonObject targetPolicy)
                table["refreshPolicy"] = targetPolicy.DeepClone();
        }
    }

    /// <summary>
    /// A refresh-policy table with no partitions fails deployment; the engine expects at least
    /// one. Injects a placeholder import partition built from the policy's source expression —
    /// the service replaces it with policy-generated partitions on the next refresh.
    /// </summary>
    internal static void AddPlaceholderPartitionsToPolicyTables(JsonObject model)
    {
        if (model["tables"] is not JsonArray tables)
            return;

        foreach (var table in tables.OfType<JsonObject>())
        {
            if (!HasPolicyWithSourceExpression(table))
                continue;
            if (table["partitions"] is JsonArray existing && existing.Count > 0)
                continue;

            var sourceExpression = ((JsonObject)table["refreshPolicy"]!)["sourceExpression"]!.DeepClone();
            table["partitions"] = new JsonArray(new JsonObject
            {
                ["name"] = $"{Name(table)}-{Guid.NewGuid():N}",
                ["mode"] = "import",
                ["source"] = new JsonObject
                {
                    ["type"] = "m",
                    ["expression"] = sourceExpression
                }
            });
        }
    }

    private static void StripRoleMemberIds(JsonObject model)
    {
        if (model["roles"] is not JsonArray roles)
            return;

        foreach (var role in roles.OfType<JsonObject>())
        {
            if (role["members"] is not JsonArray members)
                continue;
            foreach (var member in members.OfType<JsonObject>())
                member.Remove("memberId");
        }
    }

    private static bool HasPolicyWithSourceExpression(JsonObject table)
        => table["refreshPolicy"] is JsonObject policy
           && policy["sourceExpression"] is not null;

    /// <summary>A query table sources its data from a query/M expression — i.e. it is neither a
    /// calculated table nor a calculation group. Tables without partitions (possible in file
    /// sources) count as query tables so target partitions can be preserved onto them.</summary>
    private static bool IsQueryTable(JsonObject table)
    {
        if (table.ContainsKey("calculationGroup"))
            return false;

        if (table["partitions"] is not JsonArray partitions)
            return true;

        foreach (var partition in partitions.OfType<JsonObject>())
        {
            var type = partition["source"]?["type"]?.GetValue<string>();
            if (string.Equals(type, "calculated", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "calculationGroup", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static int CompatibilityLevel(JsonObject database)
        => database["compatibilityLevel"] is JsonValue level && level.TryGetValue<int>(out var value)
            ? value
            : 1500;

    private static string? Name(JsonObject obj)
        => obj["name"]?.GetValue<string>();

    private static JsonObject? FindByName(JsonArray? collection, string? name)
        => name is null
            ? null
            : collection?.OfType<JsonObject>()
                .FirstOrDefault(o => string.Equals(Name(o), name, StringComparison.OrdinalIgnoreCase));

    private static void RemoveByName(JsonArray collection, string? name)
    {
        if (FindByName(collection, name) is JsonObject match)
            collection.Remove(match);
    }
}
