// Spike: can `tx deploy` validate inside an explicit XMLA transaction and roll back?
// See spikes/deploy-transaction/README.md. Throwaway: not in Tomix.slnx, not shipped.
//
// Usage (after `.\tx.ps1 auth login`):
//   dotnet run --project spikes/deploy-transaction -- "powerbi://api.powerbi.com/v1.0/myorg/<Workspace>" [--keep]
//
// Creates and drops datasets named txspike-new-<stamp> and txspike-existing-<stamp>.

using System.Diagnostics;
using System.Text;
using Microsoft.AnalysisServices.AdomdClient;
using Microsoft.AnalysisServices.Tabular;
using Tomix.App.Config;
using Tomix.Auth;
using Tomix.Core.Models;
using Tomix.Platform.Configuration;
using Tomix.Provider.Tom;
using AsAccessToken = Microsoft.AnalysisServices.AccessToken;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: DeployTransactionSpike <xmla-endpoint> [--keep] [--sample <tmdl-folder>]");
    return 2;
}

var endpoint = ModelReference.NormalizeEndpoint(args[0]);
var keep = args.Contains("--keep");
var sampleIndex = Array.IndexOf(args, "--sample");
var samplePath = sampleIndex >= 0 ? args[sampleIndex + 1] : FindSample();
var stamp = DateTime.UtcNow.ToString("MMddHHmmss");
var newDb = $"txspike-new-{stamp}";
var existingDb = $"txspike-existing-{stamp}";

var config = new TomixConfigStore(TomixPaths.ConfigFile).Load();
var tokens = new MsalAuthenticator(AuthSettingsFactory.Resolve(config), messageWriter: Console.Error.WriteLine);

var good = TmdlSerializer.DeserializeDatabaseFromFolder(samplePath);
var broken = good.Clone();
broken.Model.Tables["Sales"].Measures["Total Amount"].Expression = "SUM ( Sales[Nope] )";

var report = new StringBuilder();
void Log(string line = "")
{
    Console.WriteLine(line);
    report.AppendLine(line);
}

Log($"# deploy transaction spike {DateTime.UtcNow:u}");
Log($"endpoint: {endpoint}");
Log($"sample:   {samplePath}");
Log();

try
{
    // ---- A0: does the endpoint accept BeginTransaction / RollbackTransaction at all? ----
    Log("## A0 explicit transaction on an idle session");
    using (var server = await ConnectAsync())
    {
        Step("BeginTransaction", server.BeginTransaction);
        Step("RollbackTransaction", server.RollbackTransaction);
        Step("BeginTransaction (2)", server.BeginTransaction);
        Step("CommitTransaction (empty)", server.CommitTransaction);
    }
    Log();

    // ---- A1: new database, broken model, rolled back ----
    Log($"## A1 new database '{newDb}': deploy broken model in a transaction, inspect, roll back");
    using (var server = await ConnectAsync())
    {
        if (Step("BeginTransaction", server.BeginTransaction))
        {
            var script = Script(broken, newDb, targetId: null);
            Step("Execute createOrReplace (broken)", () => Execute(server, script));
            InspectInSession(server, newDb, "mid-transaction, after createOrReplace");
            Step("Execute refresh calculate", () => Execute(server, RefreshCalculate(newDb)));
            InspectInSession(server, newDb, "mid-transaction, after refresh calculate");
            Step("RollbackTransaction", server.RollbackTransaction);
        }
    }
    await InspectFresh(newDb, "after rollback, fresh connection (expect: not found)");
    Log();

    // ---- A2: existing database, broken model, rolled back ----
    Log($"## A2 existing database '{existingDb}': good model committed, then broken model in a transaction, roll back");
    using (var server = await ConnectAsync())
    {
        Step("Execute createOrReplace (good, autocommit)", () => Execute(server, Script(good, existingDb, targetId: null)));
    }
    await InspectFresh(existingDb, "baseline after good deploy");
    using (var server = await ConnectAsync())
    {
        var id = server.Databases.FindByName(existingDb)?.ID;
        if (Step("BeginTransaction", server.BeginTransaction))
        {
            Step("Execute createOrReplace (broken)", () => Execute(server, Script(broken, existingDb, id)));
            InspectInSession(server, existingDb, "mid-transaction, after createOrReplace");
            Step("Execute refresh calculate", () => Execute(server, RefreshCalculate(existingDb)));
            InspectInSession(server, existingDb, "mid-transaction, after refresh calculate");
            Step("RollbackTransaction", server.RollbackTransaction);
        }
    }
    await InspectFresh(existingDb, "after rollback, fresh connection (expect: good expression, no errors)");
    Log();

    // ---- A3: control — same as A2 but commit, so the post-commit state is known ----
    Log($"## A3 control on '{existingDb}': broken model in a transaction, commit");
    using (var server = await ConnectAsync())
    {
        var id = server.Databases.FindByName(existingDb)?.ID;
        if (Step("BeginTransaction", server.BeginTransaction))
        {
            Step("Execute createOrReplace (broken)", () => Execute(server, Script(broken, existingDb, id)));
            InspectInSession(server, existingDb, "mid-transaction, after createOrReplace");
            Step("CommitTransaction", server.CommitTransaction);
        }
    }
    await InspectFresh(existingDb, "after commit, fresh connection (expect: SemanticError on Sales[Total Amount])");
    Log();
}
finally
{
    if (!keep)
    {
        Log("## cleanup");
        using var server = await ConnectAsync();
        foreach (var name in new[] { newDb, existingDb })
        {
            if (server.Databases.FindByName(name) is not null)
                Step($"delete '{name}'", () => Execute(server, """{"delete":{"object":{"database":""" + System.Text.Json.JsonSerializer.Serialize(name) + "}}}"));
        }
    }
    var reportPath = Path.Combine(Path.GetTempPath(), $"tx-deploy-transaction-spike-{stamp}.md");
    File.WriteAllText(reportPath, report.ToString());
    Console.WriteLine();
    Console.WriteLine($"report written to {reportPath}");
}

return 0;

// ------------------------------------------------------------------------------------------

async Task<Server> ConnectAsync()
{
    var server = new Server();
    if (ModelReference.RequiresAccessTokenFor(endpoint))
    {
        var token = await tokens.GetTokenAsync(ModelReference.DataSourceOf(endpoint), CancellationToken.None);
        server.AccessToken = new AsAccessToken(token.Token, token.ExpiresOn.UtcDateTime);
    }
    server.Connect(XmlaConnectionString.Build(endpoint));
    return server;
}

bool Step(string name, Action action)
{
    var sw = Stopwatch.StartNew();
    try
    {
        action();
        Log($"- {name}: ok ({sw.ElapsedMilliseconds} ms)");
        return true;
    }
    catch (Exception ex)
    {
        Log($"- {name}: FAILED ({sw.ElapsedMilliseconds} ms) {ex.GetType().Name}: {OneLine(ex)}");
        return false;
    }
}

static void Execute(Server server, string tmsl)
{
    var results = server.Execute(tmsl);
    var errors = new List<string>();
    foreach (Microsoft.AnalysisServices.XmlaResult result in results)
    {
        foreach (Microsoft.AnalysisServices.XmlaMessage message in result.Messages)
        {
            if (message is Microsoft.AnalysisServices.XmlaError)
                errors.Add(message.Description);
        }
    }
    if (errors.Count > 0)
        throw new InvalidOperationException(string.Join("; ", errors));
}

string Script(Database source, string name, string? targetId)
{
    var clone = source.Clone();
    // Same shape TmslDeployScriptBuilder emits for a full deploy: createOrReplace addressed by
    // name, the existing ID kept. deploy-qa has no role members, so no member IDs to strip.
    clone.Name = name;
    clone.ID = targetId ?? name;
    return JsonScripter.ScriptCreateOrReplace(clone);
}

static string RefreshCalculate(string name)
    => """{"refresh":{"type":"calculate","objects":[{"database":""" + System.Text.Json.JsonSerializer.Serialize(name) + "}]}}";

// Two independent reads of the session's view: AMO discover (what TomDeployValidator sees
// after server.Refresh / database.Refresh(true)) and the TMSCHEMA DMV over ADOMD bound to the
// same session ID.
void InspectInSession(Server server, string name, string label)
{
    Log($"  inspect [{label}]");
    try
    {
        server.Refresh();
        var db = server.Databases.FindByName(name);
        if (db is null)
        {
            Log("    AMO: database not visible in this session");
        }
        else
        {
            db.Refresh(true);
            var m = db.Model.Tables["Sales"].Measures["Total Amount"];
            Log($"    AMO: Sales[Total Amount] expression=`{m.Expression}` state={m.State} error={m.ErrorMessage ?? "-"}");
            var v = TomDeployValidator.Inspect(db);
            Log($"    AMO: TomDeployValidator errors={v.Errors} warnings={v.Warnings} unprocessed={v.Unprocessed}");
            foreach (var issue in v.Issues.Where(i => i.Severity == Tomix.Core.Rules.RuleSeverity.Error))
                Log($"      {issue.ObjectType} {issue.ObjectName} {issue.State}: {issue.Message}");
        }
    }
    catch (Exception ex)
    {
        Log($"    AMO: FAILED {ex.GetType().Name}: {OneLine(ex)}");
    }

    try
    {
        using var conn = new AdomdConnection(XmlaConnectionString.Build(endpoint, name));
        if (server.AccessToken.Token is { } t)
            conn.AccessToken = new AsAccessToken(t, server.AccessToken.ExpirationTime);
        conn.SessionID = server.SessionID;
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT [Name], [Expression], [State], [ErrorMessage] FROM $SYSTEM.TMSCHEMA_MEASURES";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            Log($"    DMV: {reader[0]} expression=`{reader[1]}` state={reader[2]} error={reader[3]}");
    }
    catch (Exception ex)
    {
        Log($"    DMV: FAILED {ex.GetType().Name}: {OneLine(ex)}");
    }
}

async Task InspectFresh(string name, string label)
{
    using var server = await ConnectAsync();
    Log($"  inspect [{label}]");
    var db = server.Databases.FindByName(name);
    if (db is null)
    {
        Log("    database not found");
        return;
    }
    db.Refresh(true);
    var m = db.Model.Tables["Sales"].Measures["Total Amount"];
    Log($"    Sales[Total Amount] expression=`{m.Expression}` state={m.State} error={m.ErrorMessage ?? "-"}");
    var v = TomDeployValidator.Inspect(db);
    Log($"    TomDeployValidator errors={v.Errors} warnings={v.Warnings} unprocessed={v.Unprocessed}");
}

static string OneLine(Exception ex)
    => (ex.InnerException?.Message ?? ex.Message).ReplaceLineEndings(" ");

static string FindSample()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "samples", "deploy-qa");
        if (Directory.Exists(candidate))
            return candidate;
    }
    throw new DirectoryNotFoundException("samples/deploy-qa not found; pass --sample <folder>.");
}
