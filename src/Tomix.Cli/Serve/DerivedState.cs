using System.Text.Json.Nodes;
using Tomix.App.Models;
using Tomix.Cli.Interactive;
using Tomix.Core.Models;

namespace Tomix.Cli.Serve;

/// <summary>What a client asked to be kept up to date on in <c>initialize</c> (docs/protocol.md, <c>diagnostics.updated</c>).</summary>
internal enum DiagnosticsInterest
{
    None,

    /// <summary>Dependencies and DAX diagnostics.</summary>
    Model,

    /// <summary>Dependencies, DAX diagnostics and BPA findings.</summary>
    WithBpa
}

/// <summary>
/// The dependency graph, DAX diagnostics and BPA findings of the host's session (ADR 0001 §7):
/// while a client asks for them, they are recomputed in full from the published snapshot a short
/// delay after the last change, off the session's lease queue, then announced with
/// <c>diagnostics.updated</c>. <c>dax.check</c> and <c>bpa.run</c> without parameters answer from
/// the results for the current version, and <c>deps.get</c> reuses the graph built for it.
/// </summary>
internal sealed class DerivedState : IAsyncDisposable
{
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(250);

    private readonly SessionHost _host;
    private readonly TimeProvider _time;
    private readonly TimeSpan _delay;
    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private ITimer? _timer;
    private Results? _results;

    public DerivedState(SessionHost host, TimeProvider time, TimeSpan delay)
    {
        _host = host;
        _time = time;
        _delay = delay;
    }

    /// <summary>How many recomputes have run, for tests of the debounce.</summary>
    internal int Recomputes { get; private set; }

    /// <summary>Recomputes after the delay, unless another change restarts it first.</summary>
    public void Schedule()
    {
        lock (_sync)
        {
            if (_closing.IsCancellationRequested)
                return;
            _timer ??= _time.CreateTimer(_ => _ = RecomputeAsync(_closing.Token), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// The recomputed answer to <paramref name="method"/> for the session's current version, or
    /// <c>null</c> when there is none yet.
    /// </summary>
    public JsonObject? Answer(string method, ILiveModelSession session)
    {
        lock (_sync)
        {
            if (_results is not { } results || !ReferenceEquals(results.Session, session) || results.Version != session.Version)
                return null;
            var answer = method switch
            {
                "dax.check" => results.DaxCheck,
                "bpa.run" => results.Bpa,
                _ => null
            };
            return answer?.DeepClone().AsObject();
        }
    }

    internal async Task RecomputeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _running.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var interest = _host.Interest;
            if (_host.Session is not { } session || interest == DiagnosticsInterest.None)
                return;

            var snapshot = await session.GetLiveSnapshotAsync(cancellationToken);
            lock (_sync)
            {
                if (_results is { } done && ReferenceEquals(done.Session, session) && done.Version == snapshot.Version
                    && (interest != DiagnosticsInterest.WithBpa || done.Bpa is not null))
                    return;
            }

            Recomputes++;
            var source = new LiveSessionSource(session, new LiveLeaseOptions("tx", "diagnostics")) { Snapshot = snapshot };
            var root = _host.BuildRoot(new SessionScope(source), []);
            var daxCheck = await RunAsync(root, "dax.check", ["validate"], snapshot.Version, cancellationToken);
            // Builds the dependency graph once for this version; deps.get reads it from there.
            await RunAsync(root, "deps.get", ["deps", "--unused"], snapshot.Version, cancellationToken);
            var bpa = interest == DiagnosticsInterest.WithBpa
                ? await RunAsync(root, "bpa.run", ["bpa", "run"], snapshot.Version, cancellationToken)
                : null;

            lock (_sync)
                _results = new Results(session, snapshot.Version, daxCheck, bpa);
            // A newer change has its own recompute coming, which announces that version instead.
            if (session.Version == snapshot.Version)
                _host.AnnounceDiagnostics(snapshot.Version);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
            // The session closed while it was being read.
        }
        catch (Exception ex)
        {
            _host.Log.WriteLine($"[tx serve] recomputing diagnostics failed: {ex.Message}");
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task<JsonObject?> RunAsync(System.CommandLine.RootCommand root, string method, string[] arguments, long version, CancellationToken cancellationToken)
    {
        try
        {
            var envelope = await ServeSession.RunJsonAsync(root, method, arguments, _host.Log, cancellationToken);
            envelope["version"] = version;
            return envelope;
        }
        catch (ProtocolException ex)
        {
            _host.Log.WriteLine($"[tx serve] {method} for version {version} failed: {ex.Message}");
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_closing.IsCancellationRequested)
                return;
            _closing.Cancel();
            _timer?.Dispose();
        }

        // Waits for a recompute that is running to stop.
        await _running.WaitAsync();
        _running.Release();
    }

    private sealed record Results(ILiveModelSession Session, long Version, JsonObject? DaxCheck, JsonObject? Bpa);
}
