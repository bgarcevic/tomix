using System.Text.Json;
using System.Text.Json.Serialization;
using Jint;
using Jint.Constraints;
using Jint.Native;

namespace Tomix.App.Format.M;

/// <summary>
/// Runs the embedded offline M engine (<see cref="PowerQueryEngineBundle"/>) in-process under Jint,
/// a pure .NET JavaScript interpreter: no Node, no network, no native dependencies. The bundle is
/// evaluated lazily on first use (about half a second) and the engine is reused, with calls
/// serialized because a Jint engine is single-threaded.
/// </summary>
/// <remarks>
/// Every call is bounded by a time budget and the caller's token. A timeout or an engine failure
/// comes back as an <see cref="PowerQueryErrorKind.Internal"/> error, never an exception; genuine
/// cancellation (Ctrl-C) rethrows so the command exits through the cancellation path. After any
/// interrupted or failed call the engine is discarded and rebuilt on the next one, so a half-run
/// script cannot leave state behind.
/// </remarks>
internal sealed class PowerQueryEngine : IDisposable
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    // Counts nested calls of one function definition. Real queries stay far below this; the
    // stack-overflow guard catches recursion the count cannot see.
    private const int MaxRecursionDepth = 1000;

    private const int EngineThreadStackSize = 256 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Func<string> _loadBundle;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Runtime? _runtime;

    public PowerQueryEngine(TimeSpan? timeout = null)
        : this(PowerQueryEngineBundle.Load, timeout)
    {
    }

    internal PowerQueryEngine(Func<string> loadBundle, TimeSpan? timeout = null)
    {
        _loadBundle = loadBundle;
        _timeout = timeout ?? DefaultTimeout;
    }

    public async Task<PowerQueryFormatResult> FormatAsync(
        string text,
        PowerQueryFormatOptions options,
        CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Serialize(
            new
            {
                text,
                indentationLiteral = options.IndentationLiteral,
                newlineLiteral = options.NewlineLiteral,
                maxWidth = options.MaxWidth
            },
            JsonOptions);

        var (json, failure) = await CallAsync(runtime => runtime.Invoke(runtime.Format, request), cancellationToken);
        if (failure is not null)
            return new PowerQueryFormatResult(false, null, failure);

        var result = JsonSerializer.Deserialize<PowerQueryFormatResult>(json!, JsonOptions);
        return result is { Ok: true, Text: not null } or { Ok: false, Error: not null }
            ? result
            : new PowerQueryFormatResult(false, null, UnexpectedResponse(json!));
    }

    public async Task<PowerQueryDiagnoseResult> DiagnoseAsync(string text, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Serialize(new { text }, JsonOptions);

        var (json, failure) = await CallAsync(runtime => runtime.Invoke(runtime.Diagnose, request), cancellationToken);
        if (failure is not null)
            return new PowerQueryDiagnoseResult([failure]);

        var result = JsonSerializer.Deserialize<PowerQueryDiagnoseResult>(json!, JsonOptions);
        return result?.Errors is not null
            ? result
            : new PowerQueryDiagnoseResult([UnexpectedResponse(json!)]);
    }

    /// <summary>The bundled formatter and parser versions, or <see langword="null"/> if the engine fails to load.</summary>
    public async Task<PowerQueryEngineVersion?> GetVersionAsync(CancellationToken cancellationToken)
    {
        var (json, failure) = await CallAsync(runtime => runtime.Version, cancellationToken);
        return failure is null
            ? JsonSerializer.Deserialize<PowerQueryEngineVersion>(json!, JsonOptions)
            : null;
    }

    public void Dispose()
    {
        Discard();
        _gate.Dispose();
    }

    private async Task<(string? Json, PowerQueryEngineError? Failure)> CallAsync(
        Func<Runtime, string> operation,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await RunWithLargeStack(() => Call(operation, cancellationToken));
        }
        finally
        {
            _gate.Release();
        }
    }

    // Interpreted frames are large: on a default 1 MB stack the parser runs out at about 100
    // levels of nesting, while Node manages about 200-500. The stack is reserved, not committed,
    // so a generous size costs address space only.
    private static Task<T> RunWithLargeStack<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(
            () =>
            {
                try
                {
                    completion.SetResult(work());
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            },
            EngineThreadStackSize)
        {
            IsBackground = true,
            Name = "tx Power Query engine"
        };
        thread.Start();
        return completion.Task;
    }

    private (string? Json, PowerQueryEngineError? Failure) Call(
        Func<Runtime, string> operation,
        CancellationToken cancellationToken)
    {
        var runtime = _runtime ??= new Runtime();
        var loading = !runtime.IsLoaded;
        runtime.Deadline.Begin(_timeout, cancellationToken);
        try
        {
            if (loading)
                runtime.Load(_loadBundle());

            return (operation(runtime), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Discard();
            throw;
        }
        catch (TimeoutException)
        {
            Discard();
            return (null, Internal($"The Power Query engine timed out after {_timeout.TotalSeconds:0.###} seconds."));
        }
        catch (Exception ex)
        {
            Discard();
            return (null, Internal(loading
                ? $"The offline Power Query engine failed to load: {ex.Message}"
                : $"The offline Power Query engine failed: {ex.Message}"));
        }
        finally
        {
            runtime.Deadline.End();
        }
    }

    private void Discard()
    {
        _runtime?.Dispose();
        _runtime = null;
    }

    private static PowerQueryEngineError Internal(string message)
        => new(PowerQueryErrorKind.Internal, message, null, null);

    private static PowerQueryEngineError UnexpectedResponse(string json)
        => Internal($"The offline Power Query engine returned an unexpected response: {json}");

    private sealed class Runtime : IDisposable
    {
        private readonly Engine _engine;

        public Runtime()
        {
            Deadline = new OperationDeadlineConstraint();
            _engine = new Engine(options =>
            {
                options.Constraint(Deadline);
                options.LimitRecursion(MaxRecursionDepth);
                // M comes from models tx did not write: deep nesting must become a catchable
                // RangeError, not a native stack overflow that ends the process.
                options.Constraints.StackOverflowGuard = true;
            });
        }

        public OperationDeadlineConstraint Deadline { get; }

        public bool IsLoaded { get; private set; }

        public JsValue Format { get; private set; } = JsValue.Undefined;

        public JsValue Diagnose { get; private set; } = JsValue.Undefined;

        public string Version { get; private set; } = "";

        public void Load(string bundle)
        {
            _engine.Execute(bundle, PowerQueryEngineBundle.ResourceName);

            var global = _engine.GetValue(PowerQueryEngineBundle.GlobalName);
            if (!global.IsObject())
                throw new InvalidOperationException($"the bundle did not define globalThis.{PowerQueryEngineBundle.GlobalName}.");

            var api = global.AsObject();
            Format = api.Get("format");
            Diagnose = api.Get("diagnose");
            Version = api.Get("version").AsString();
            IsLoaded = true;
        }

        // The engine's operations are async functions that never reject, so the promise has
        // settled by the time Invoke returns.
        public string Invoke(JsValue function, string requestJson)
            => _engine.Invoke(function, requestJson).UnwrapIfPromise().AsString();

        public void Dispose() => _engine.Dispose();
    }
}

internal sealed record PowerQueryFormatOptions(string IndentationLiteral, string NewlineLiteral, int MaxWidth);

[JsonConverter(typeof(JsonStringEnumConverter<PowerQueryErrorKind>))]
internal enum PowerQueryErrorKind
{
    Lex,
    Parse,
    Internal
}

/// <summary>An engine error. <see cref="Line"/> and <see cref="Column"/> are 1-based, or null when the error has no position.</summary>
internal sealed record PowerQueryEngineError(PowerQueryErrorKind Kind, string Message, int? Line, int? Column);

/// <summary><see cref="Text"/> is set when <see cref="Ok"/>; otherwise <see cref="Error"/> is. Formatted text ends with a newline.</summary>
internal sealed record PowerQueryFormatResult(bool Ok, string? Text, PowerQueryEngineError? Error);

/// <summary>Empty <see cref="Errors"/> means the text lexes and parses.</summary>
internal sealed record PowerQueryDiagnoseResult(IReadOnlyList<PowerQueryEngineError> Errors);

internal sealed record PowerQueryEngineVersion(string Formatter, string Parser);
