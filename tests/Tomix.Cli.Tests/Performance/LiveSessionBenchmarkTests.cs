using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Tomix.App.Format;
using Tomix.Cli.Interactive;
using Tomix.Cli.Serve;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;
using Tomix.Provider.Tom;
using Xunit.Abstractions;

namespace Tomix.Cli.Tests.Performance;

/// <summary>
/// The live-session performance benchmark (#352, ADR 0004): the protocol methods a client uses
/// most, timed through the same dispatcher as <c>tx serve</c>, <c>tx mcp</c> and <c>tx ui</c>, on
/// <see cref="LargeModel"/>. Opt-in, because timings belong on a quiet machine and not in the
/// default test run:
/// <c>TOMIX_PERF=1 dotnet test tests/Tomix.Cli.Tests --filter FullyQualifiedName~LiveSessionBenchmark --logger "console;verbosity=detailed"</c>.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class LiveSessionBenchmarkTests(ITestOutputHelper output)
{
    private static readonly IReadOnlyList<IModelProvider> Providers = [new TmdlModelProvider(), new TomFileModelProvider()];
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>The budgets of a warm session (ADR 0004), checked against the median.</summary>
    private static readonly Dictionary<string, double> Budgets = new(StringComparer.Ordinal)
    {
        ["object.set + model.changed"] = 50,
        ["session.undo"] = 50,
        ["model.tree (root + one table)"] = 30,
    };

    private const int Warmup = 3;
    private const int Iterations = 20;
    private const int HeavyIterations = 5;

    [PerfFact]
    public async Task LargeModel_StaysWithinTheWarmSessionBudgets()
    {
        using var model = new TempDir();
        LargeModel.WriteTmdl(model.Path);
        var log = TextWriter.Synchronized(new StringWriter());
        using var routing = ConsoleRouting.Install(log);
        var services = TestServices.Create();
        var formatter = new CompositeExpressionFormatterClient([new OfflineDaxFormatterClient()]);
        var opener = new SessionOpener(Providers, services.State, services.Staging);
        var results = new List<Row>();

        var watch = Stopwatch.StartNew();
        var (session, failure) = await opener.TryOpenAsync(opener.Resolve(model.Path, null, null), showSpinner: false, CancellationToken.None);
        watch.Stop();
        Assert.True(session is not null, failure?.Message);
        results.Add(Row.Of("session open (cold)", [watch.Elapsed.TotalMilliseconds]));

        // The recompute never fires on its own, so it runs only where it is timed.
        var host = new SessionHost(
            (scope, commands) => Program.BuildSessionRootCommand(scope, commands, Providers, formatter, services, httpClient: null),
            opener,
            log,
            session,
            diagnosticsDelay: Timeout.InfiniteTimeSpan);
        try
        {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var client = host.Connect();
            client.Attach("bench", new JsonObject { ["diagnostics"] = new JsonObject { ["bpa"] = true } }, (method, _) =>
            {
                if (method == "model.changed")
                    changed.TrySetResult();
            });

            var edit = 0;
            async Task SetAsync()
            {
                changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var t = edit % LargeModel.Tables;
                var path = $"{LargeModel.TableName(t)}/{LargeModel.MeasureName(t, 0)}";
                await Invoke(client, "object.set", new JsonObject
                {
                    ["path"] = path,
                    ["set"] = new JsonObject { ["expression"] = $"SUM('{LargeModel.TableName(t)}'[Value 02]) + {edit++}" }
                });
                await changed.Task.WaitAsync(Patience);
            }

            results.Add(await MeasureAsync("object.set + model.changed", Iterations, SetAsync));
            results.Add(await MeasureAsync("session.undo", Iterations, () => Invoke(client, "session.undo", []), before: SetAsync));
            results.Add(await MeasureAsync("model.tree (root + one table)", Iterations, async () =>
            {
                await Invoke(client, "model.tree", []);
                await Invoke(client, "model.tree", new JsonObject { ["path"] = LargeModel.TableName(LargeModel.Tables / 2) });
            }));
            results.Add(await MeasureAsync("snapshot rebuild after an edit", HeavyIterations,
                () => session.GetLiveSnapshotAsync(CancellationToken.None), before: SetAsync));
            results.Add(await MeasureAsync("bpa.run (cold, after an edit)", HeavyIterations,
                () => Invoke(client, "bpa.run", []), before: SetAsync));
            results.Add(await MeasureAsync("diagnostics recompute (deps, DAX, BPA)", HeavyIterations,
                () => host.Diagnostics.RecomputeAsync(CancellationToken.None), before: SetAsync));
            results.Add(await MeasureAsync("bpa.run (warm, recomputed)", Iterations, async () =>
            {
                Assert.NotNull(host.Diagnostics.Answer("bpa.run", session));
                await Invoke(client, "bpa.run", []);
            }));
            results.Add(await MeasureAsync("session.save", HeavyIterations, () => Invoke(client, "session.save", []), before: SetAsync));

            // The same edit without the command layer: lease, set, commit (ADR 0002).
            results.Add(await MeasureAsync("provider set + commit (no command layer)", Iterations, async () =>
            {
                await using var lease = await session.BeginTransactionAsync(new LiveLeaseOptions("bench"), CancellationToken.None);
                ((IModelMutationSession)lease.Session).SetProperty(new ModelObjectSetRequest(
                    $"{LargeModel.TableName(1)}/{LargeModel.MeasureName(1, 0)}", [new ModelPropertyAssignment("Expression", $"{edit++}")], null));
                await lease.CommitAsync(CancellationToken.None);
            }));

            // What every writing transaction and every undo pays for its checkpoint (ADR 0003).
            var database = Microsoft.AnalysisServices.Tabular.TmdlSerializer.DeserializeDatabaseFromFolder(model.Path);
            results.Add(await MeasureAsync("TOM Database.Clone (one checkpoint)", Iterations, () =>
            {
                database.Clone();
                return Task.CompletedTask;
            }));
        }
        finally
        {
            await host.CloseAsync();
        }

        output.WriteLine(Report(results));
        var missed = results.Where(row => Budgets.TryGetValue(row.Operation, out var budget) && row.Median >= budget).ToList();
        Assert.True(missed.Count == 0, "Over budget: " + string.Join(", ", missed.Select(row => $"{row.Operation} {Ms(row.Median)} ms (budget {Ms(Budgets[row.Operation])} ms)")));
    }

    /// <summary>A failed request throws <see cref="ProtocolException"/>, which fails the benchmark.</summary>
    private static Task Invoke(ServeSession client, string method, JsonObject parameters)
        => client.InvokeAsync(method, parameters, CancellationToken.None);

    /// <summary>
    /// Runs <paramref name="operation"/> <see cref="Warmup"/> times untimed, then
    /// <paramref name="iterations"/> times timed; <paramref name="before"/> runs untimed before each.
    /// </summary>
    private static async Task<Row> MeasureAsync(string name, int iterations, Func<Task> operation, Func<Task>? before = null)
    {
        var samples = new List<double>(iterations);
        for (var i = 0; i < Warmup + iterations; i++)
        {
            if (before is not null)
                await before();
            var watch = Stopwatch.StartNew();
            await operation();
            watch.Stop();
            if (i >= Warmup)
                samples.Add(watch.Elapsed.TotalMilliseconds);
        }

        return Row.Of(name, samples);
    }

    private static string Report(IReadOnlyList<Row> rows)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Large model: {LargeModel.Tables} tables, {LargeModel.Tables * LargeModel.ColumnsPerTable} columns, {LargeModel.Tables * LargeModel.MeasuresPerTable} measures");
        text.AppendLine("| Operation | Runs | Median (ms) | p95 (ms) | Budget (ms) |");
        text.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var row in rows)
        {
            var budget = Budgets.TryGetValue(row.Operation, out var ms) ? $"< {Ms(ms)}" : "-";
            text.AppendLine(CultureInfo.InvariantCulture, $"| {row.Operation} | {row.Runs} | {Ms(row.Median)} | {Ms(row.P95)} | {budget} |");
        }

        return text.ToString();
    }

    private static string Ms(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private sealed record Row(string Operation, int Runs, double Median, double P95)
    {
        public static Row Of(string operation, IReadOnlyList<double> samples)
        {
            var sorted = samples.Order().ToArray();
            return new Row(operation, sorted.Length, sorted[sorted.Length / 2], sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1]);
        }
    }
}

/// <summary>A test that runs only when <c>TOMIX_PERF=1</c>, so timings stay out of the default run.</summary>
internal sealed class PerfFactAttribute : FactAttribute
{
    public PerfFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TOMIX_PERF") != "1")
            Skip = "Performance benchmark; set TOMIX_PERF=1 to run it.";
    }
}
