using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Spectre.Console;
using Tomix.App.State;
using Tomix.Cli.Commands;
using Tomix.Cli.Output;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;

namespace Tomix.Cli.Serve;

/// <summary>
/// A one-shot command on a model a <c>tx ui</c> holds (#400): rather than open the files, which the
/// session would later overwrite, it runs in that session as a short-lived client, through
/// <c>command.run</c>. Its edits become undo steps there and stay unsaved until a save, as in
/// <c>tx interactive</c>. Paths are made absolute first, since the session runs in its own folder.
/// </summary>
internal sealed class LiveCommandRoute(
    LiveRegistry registry,
    CliStateStore state,
    Func<LiveEntry, CancellationToken, Task<WebSocket>>? connect = null)
{
    /// <summary>The commands that run in a live session: those of its command tree that address the model.</summary>
    public static readonly IReadOnlyList<string> Commands =
        ["add", "bpa run", "deps", "find", "format", "get", "ls", "mv", "replace", "rm", "save", "set", "summary", "validate"];

    /// <summary>Whether <paramref name="command"/> can change the model, so its edits wait for a save.</summary>
    public static bool Edits(string command)
        => command is "add" or "bpa run" or "format" or "mv" or "replace" or "rm" or "set";

    /// <summary>The options and arguments whose values are local paths, read relative to the folder tx runs in.</summary>
    private static readonly HashSet<string> PathSymbols = new(StringComparer.Ordinal)
    {
        "model", "--model", "--file", "--save-to", "--output-file", "--trx", "--rules", "--bpa-rules"
    };

    private readonly Func<LiveEntry, CancellationToken, Task<WebSocket>> _connect = connect ?? ServeRelay.ConnectAsync;

    /// <summary><c>bpa run</c> for that command: the names of the commands under the root.</summary>
    public static string CommandPath(CommandResult result)
    {
        var names = new List<string>();
        for (var current = result; current.Parent is CommandResult parent; current = parent)
            names.Insert(0, current.Command.Name);
        return string.Join(' ', names);
    }

    /// <summary>
    /// The command line to run in the live session holding the command's model, or <c>null</c> when
    /// the command runs on its own: it is not one of <see cref="Commands"/>, it names its model with
    /// <c>--recent</c> (which prompts) or a server, or no session has the model open.
    /// </summary>
    public Routed? Plan(ParseResult parseResult)
    {
        if (!Commands.Contains(CommandPath(parseResult.CommandResult)) || GlobalOptions.RecentSpecified(parseResult))
            return null;

        var modelToken = ModelToken(parseResult);
        var explicitModel = modelToken?.Value;
        if (!string.IsNullOrWhiteSpace(explicitModel) && !ModelReference.IsRemoteEndpoint(explicitModel))
            explicitModel = Absolute(explicitModel);
        var reference = new ActiveModelResolver(state).ResolveReference(
            explicitModel, parseResult.GetValue(GlobalOptions.Database), parseResult.GetValue(GlobalOptions.Server));
        if (!reference.IsLocalPath || string.IsNullOrWhiteSpace(reference.Value) || registry.Find(reference.Value) is not { } entry)
            return null;

        var paths = PathTokens(parseResult);
        var args = new List<string>();
        foreach (var token in parseResult.Tokens)
            args.Add(paths.Contains(token) ? Absolute(token.Value) : token.Value);
        // The session's model unless the command says otherwise: always the one resolved here.
        if (modelToken is null)
            args.InsertRange(0, ["--model", reference.Value]);

        var readsStdin = parseResult.Tokens.Any(token => token.Value == "-" || token.Value.EndsWith("=-", StringComparison.Ordinal));
        return new Routed(entry, args, readsStdin ? InputValueResolver.Resolve("-") : null, CommandPath(parseResult.CommandResult));
    }

    /// <summary>
    /// Runs <paramref name="routed"/> in its session and prints what it printed there; returns its
    /// exit code, or 2 when the session cannot be reached.
    /// </summary>
    public async Task<int> RunAsync(Routed routed, ParseResult parseResult, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var errorFormat = GlobalOptions.ErrorFormatValue(parseResult);
        WebSocket socket;
        try
        {
            socket = await _connect(routed.Session, cancellationToken);
        }
        catch (WebSocketException ex)
        {
            ErrorOutput.Write(
                [Unreachable(routed.Session, ex.Message)],
                errorFormat);
            return 2;
        }

        using (socket)
        {
            var channel = new WebSocketChannel(socket);
            var client = new ProtocolClient(channel);
            JsonObject result;
            try
            {
                await client.RequestAsync("initialize", new JsonObject
                {
                    ["protocolVersion"] = "0",
                    ["clientInfo"] = new JsonObject { ["name"] = "tx " + routed.Command }
                }, cancellationToken);
                var run = await client.RequestAsync("command.run", new JsonObject
                {
                    ["args"] = new JsonArray([.. routed.Args.Select(arg => (JsonNode)arg)]),
                    ["stdin"] = routed.Stdin,
                    ["colorSystem"] = AnsiConsole.Profile.Capabilities.ColorSystem.ToString(),
                    ["width"] = Console.IsOutputRedirected ? null : AnsiConsole.Profile.Width
                }, cancellationToken);
                result = run["data"]!.AsObject();
                await client.RequestAsync("shutdown", null, cancellationToken);
                client.Notify("exit");
                // The session closes the socket once it has let the client go: then tx has left it.
                using var patience = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                patience.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    while (await channel.ReadAsync(patience.Token) is not null)
                    {
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }
            }
            catch (ProtocolException ex)
            {
                var data = ex.ErrorData;
                ErrorOutput.Write(
                    [new TomixDiagnostic(
                        (string?)data?["code"] ?? "TOMIX_UI_UNREACHABLE",
                        DiagnosticSeverity.Error,
                        ex.Message,
                        (string?)data?["hint"] ?? $"Run the command in tx ui's session another way, or stop tx ui (process {routed.Session.ProcessId}).")],
                    errorFormat);
                return (int?)data?["exitCode"] ?? 2;
            }
            catch (InvalidOperationException ex)
            {
                // The session closed before it answered: tx ui stopped.
                ErrorOutput.Write([Unreachable(routed.Session, ex.Message)], errorFormat);
                return 2;
            }
            finally
            {
                await channel.CompleteAsync();
            }

            stdout.Write((string?)result["stdout"]);
            stderr.Write((string?)result["stderr"]);
            return (int?)result["exitCode"] ?? 1;
        }
    }

    private static TomixDiagnostic Unreachable(LiveEntry entry, string reason)
        => new(
            "TOMIX_UI_UNREACHABLE",
            DiagnosticSeverity.Error,
            $"{entry.Model} is open in tx ui (process {entry.ProcessId}), but its session cannot be reached: {reason}",
            "Stop that tx ui, or wait for it to start, then try again.");

    /// <summary>The token naming the model: <c>--model</c>'s value, else the command's <c>model</c> argument.</summary>
    private static Token? ModelToken(ParseResult parseResult)
    {
        foreach (var symbol in Symbols(parseResult))
        {
            if (symbol is OptionResult { Option.Name: "--model" } option && option.Tokens.Count > 0)
                return option.Tokens[^1];
        }

        return Symbols(parseResult).OfType<ArgumentResult>().FirstOrDefault(argument => argument.Argument.Name == "model" && argument.Tokens.Count > 0)?.Tokens[0];
    }

    /// <summary>The tokens that are values of <see cref="PathSymbols"/>, by reference.</summary>
    private static HashSet<Token> PathTokens(ParseResult parseResult)
    {
        var tokens = new HashSet<Token>(ReferenceEqualityComparer.Instance);
        foreach (var symbol in Symbols(parseResult))
        {
            var name = symbol switch
            {
                OptionResult option => option.Option.Name,
                ArgumentResult argument => argument.Argument.Name,
                _ => null
            };
            if (name is not null && PathSymbols.Contains(name))
                tokens.UnionWith(symbol.Tokens);
        }

        return tokens;
    }

    /// <summary>The options and arguments of the command and of the commands above it.</summary>
    private static IEnumerable<SymbolResult> Symbols(ParseResult parseResult)
    {
        for (SymbolResult? command = parseResult.CommandResult; command is not null; command = command.Parent)
            foreach (var child in ((CommandResult)command).Children)
                if (child is OptionResult or ArgumentResult)
                    yield return child;
    }

    /// <summary>A local path made absolute; a URL, <c>-</c> (stdin), nothing or a value that is no path as given.</summary>
    private static string Absolute(string value)
    {
        if (value.Length == 0 || value == "-" || value.Contains("://", StringComparison.Ordinal))
            return value;
        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return value;
        }
    }

    /// <summary>A command to run in <see cref="Session"/>.</summary>
    /// <param name="Session">The live session holding the command's model.</param>
    /// <param name="Args">The command line, its paths absolute and its model named.</param>
    /// <param name="Stdin">What the command reads for a <c>-</c> value, read from tx's own stdin.</param>
    /// <param name="Command">The command, for example <c>bpa run</c>, which names the client.</param>
    internal sealed record Routed(LiveEntry Session, IReadOnlyList<string> Args, string? Stdin, string Command);
}
