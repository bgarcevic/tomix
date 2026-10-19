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

// --drop <name>: delete one dataset the manual `tx deploy` checks left behind, then exit.
var dropIndex = Array.IndexOf(args, "--drop");
if (dropIndex >= 0)
{
    using var dropServer = await ConnectAsync();
    Execute(dropServer, """{"delete":{"object":{"database":""" + System.Text.Json.JsonSerializer.Serialize(args[dropIndex + 1]) + "}}}");
    Console.WriteLine($"deleted '{args[dropIndex + 1]}'");
    return 0;
}

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

    // ---- A4: existing database, no reads at all between execute and rollback ----
    Log($"## A4 '{existingDb}': restore good model, then begin, deploy broken, roll back immediately");
    using (var server = await ConnectAsync())
    {
        var id = server.Databases.FindByName(existingDb)?.ID;
        Step("Execute createOrReplace (good, autocommit)", () => Execute(server, Script(good, existingDb, id)));
    }
    using (var server = await ConnectAsync())
    {
        var id = server.Databases.FindByName(existingDb)?.ID;
        if (Step("BeginTransaction", server.BeginTransaction))
        {
            Step("Execute createOrReplace (broken)", () => Execute(server, Script(broken, existingDb, id)));
            Step("RollbackTransaction", server.RollbackTransaction);
        }
    }
    await InspectFresh(existingDb, "after rollback, fresh connection (expect: good expression, no errors)");
    Log();

    // ---- A5: object-level change (TOM SaveChanges = alter, not database createOrReplace) ----
    Log($"## A5 '{existingDb}': restore good model, then begin, alter one measure via TOM SaveChanges, inspect, roll back");
    using (var server = await ConnectAsync())
    {
        var id = server.Databases.FindByName(existingDb)?.ID;
        Step("Execute createOrReplace (good, autocommit)", () => Execute(server, Script(good, existingDb, id)));
    }
    using (var server = await ConnectAsync())
    {
        server.Refresh();
        var db = server.Databases.FindByName(existingDb);
        Log($"- database visible: {db is not null} (databases in workspace: {server.Databases.Count})");
        foreach (Microsoft.AnalysisServices.Tabular.Database d in server.Databases)
            if (d.Name.StartsWith("txspike", StringComparison.Ordinal))
                Log($"    '{d.Name}' id={d.ID}");
        db?.Refresh(true);
        if (db is not null && Step("BeginTransaction", server.BeginTransaction))
        {
            Step("SaveChanges (measure -> SUM ( Sales[Nope] ))", () =>
            {
                db.Model.Tables["Sales"].Measures["Total Amount"].Expression = "SUM ( Sales[Nope] )";
                db.Model.SaveChanges();
            });
            InspectInSession(server, existingDb, "mid-transaction, after SaveChanges");
            Step("RollbackTransaction", server.RollbackTransaction);
        }
    }
    await InspectFresh(existingDb, "after rollback, fresh connection (expect: good expression, no errors)");
    Log();

    // ---- A6/A7: whole-model deploy as object-level changes (Model.CopyTo + SaveChanges) ----
    Log($"## A6 '{existingDb}': restore + process good model, then begin, CopyTo(broken model), SaveChanges, inspect, roll back");
    using (var server = await ConnectAsync())
    {
        server.Refresh();
        var id = server.Databases.FindByName(existingDb)?.ID;
        Step("Execute createOrReplace (good, autocommit)", () => Execute(server, Script(good, existingDb, id)));
        Step("Execute refresh full (autocommit)", () => Execute(server, RefreshType(existingDb, "full")));
    }
    await RowCount(existingDb, "baseline");
    await CopyToInTransaction(broken, commit: false, "broken");
    await InspectFresh(existingDb, "after rollback, fresh connection (expect: good expression, no errors)");
    await RowCount(existingDb, "after rollback (expect: baseline)");
    Log();

    Log($"## A7 '{existingDb}': begin, CopyTo(good model + changed description), SaveChanges, inspect, commit");
    var described = good.Clone();
    described.Model.Tables["Sales"].Measures["Total Amount"].Description = $"spike {stamp}";
    await CopyToInTransaction(described, commit: true, "good + description");
    await InspectFresh(existingDb, "after commit, fresh connection (expect: no errors)");
    await RowCount(existingDb, "after commit (expect: baseline, data kept)");
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
        // Close(endSession: false): the session belongs to the AMO server, and a plain
        // Close/Dispose would end it (and with it the open transaction).
        var conn = new AdomdConnection(XmlaConnectionString.Build(endpoint, name));
        try
        {
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
        finally
        {
            conn.Close(false);
        }
    }
    catch (Exception ex)
    {
        Log($"    DMV: FAILED {ex.GetType().Name}: {OneLine(ex)}");
    }
}

async Task InspectFresh(string name, string label)
{
    Log($"  inspect [{label}]");
    // The service's database list lags a few seconds behind a change; retry on new connections.
    Server? server = null;
    Database? db = null;
    for (var attempt = 1; attempt <= 6 && db is null; attempt++)
    {
        server?.Dispose();
        if (attempt > 1)
            await Task.Delay(TimeSpan.FromSeconds(5));
        server = await ConnectAsync();
        db = server.Databases.FindByName(name);
        if (db is null)
            Log($"    attempt {attempt}: database not listed yet");
    }
    using var owned = server!;
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

async Task CopyToInTransaction(Database source, bool commit, string label)
{
    using var server = await ConnectAsync();
    server.Refresh();
    var db = server.Databases.FindByName(existingDb);
    if (db is null)
    {
        Log("- target not visible; skipped");
        return;
    }
    db.Refresh(true);
    if (!Step("BeginTransaction", server.BeginTransaction))
        return;
    Step($"CopyTo ({label}) + SaveChanges", () =>
    {
        // Merge exactly as tx deploy does (Preserve: target partitions, roles, connections
        // stay), then copy the merged model over the live one so TOM sends only the diff.
        var clone = source.Clone();
        clone.Name = db.Name;
        var restricted = new SerializeOptions { IncludeRestrictedInformation = true };
        var builder = typeof(TomModelDeployer).Assembly.GetType("Tomix.Provider.Tom.TmslDeployScriptBuilder")!;
        var node = builder.GetMethod("BuildDatabaseNode", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [JsonSerializer.SerializeDatabase(clone, restricted), JsonSerializer.SerializeDatabase(db, restricted),
                db.Name, db.ID, ModelDeployOptions.Preserve, true])!;
        var merged = JsonSerializer.DeserializeDatabase(node.ToString()!);
        merged.Model.CopyTo(db.Model);
        var result = db.Model.SaveChanges();
        Log($"    SaveChanges xmla results: {result.XmlaResults?.Count ?? 0}");
    });
    InspectInSession(server, existingDb, "mid-transaction, after SaveChanges");
    var validation = TomDeployValidator.Inspect(db);
    if (commit && validation.Errors == 0)
        Step("CommitTransaction", server.CommitTransaction);
    else
        Step($"RollbackTransaction (errors={validation.Errors})", server.RollbackTransaction);
}

async Task RowCount(string name, string label)
{
    using var server = await ConnectAsync();
    var conn = new AdomdConnection(XmlaConnectionString.Build(endpoint, name));
    try
    {
        if (server.AccessToken.Token is { } t)
            conn.AccessToken = new AsAccessToken(t, server.AccessToken.ExpirationTime);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "EVALUATE ROW(\"customers\", COUNTROWS(Customers), \"sales\", COUNTROWS(Sales))";
        using var reader = cmd.ExecuteReader();
        reader.Read();
        Log($"  rows [{label}]: Customers={reader[0]} Sales={reader[1]}");
    }
    catch (Exception ex)
    {
        Log($"  rows [{label}]: FAILED {OneLine(ex)}");
    }
    finally
    {
        conn.Close();
    }
}

static string RefreshType(string name, string type)
    => """{"refresh":{"type":""" + System.Text.Json.JsonSerializer.Serialize(type) + ""","objects":[{"database":""" + System.Text.Json.JsonSerializer.Serialize(name) + "}]}}";

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
