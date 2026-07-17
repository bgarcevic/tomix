namespace Tomix.App.Query;

/// <param name="Model">Raw --model value (local path or remote endpoint). Null = use active session.</param>
/// <param name="Server">Explicit -s/--server override.</param>
/// <param name="Database">Explicit -d/--database override.</param>
/// <param name="Auth">--auth hint (auto/interactive/spn/env/managed-identity); resolved by the provider via token.</param>
/// <param name="Query">Query text (DAX EVALUATE/DEFINE or DMV SELECT), already resolved from -q/--file/stdin.</param>
/// <param name="Parameters">--param name=value pairs, referenced as <c>@name</c> in DAX.</param>
/// <param name="Limit">--limit: client-side row cap.</param>
/// <param name="NoValidate">--no-validate: skip the leading-keyword pre-check.</param>
/// <param name="Trace">--trace: capture server timings (formula- vs storage-engine).</param>
/// <param name="TracePath">Raw XMLA trace dump target: null = off, "-" = stderr, path = file.</param>
/// <param name="Plan">--plan: capture the logical and physical DAX query plans.</param>
/// <param name="Cold">--cold: clear the model cache before each run.</param>
/// <param name="Runs">--runs: number of executions (>= 1); &gt; 1 produces a benchmark summary.</param>
public sealed record QueryModelRequest(
    string? Model,
    string? Server,
    string? Database,
    string? Auth,
    string? Query,
    IReadOnlyDictionary<string, string>? Parameters,
    int? Limit,
    bool NoValidate,
    bool Trace = false,
    string? TracePath = null,
    bool Plan = false,
    bool Cold = false,
    int Runs = 1);
