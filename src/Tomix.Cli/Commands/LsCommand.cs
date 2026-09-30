using System.CommandLine;
using Tomix.App.Get;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

internal sealed class LsCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    private readonly CliStateStore _state;

    public LsCommand(IReadOnlyList<IModelProvider> providers, CliStateStore state)
    {
        _providers = providers;
        _state = state;
    }

    public Command Build()
    {
        var pathArgument = new Argument<string?>("path-filter")
        {
            Description =
                "Which objects to list: a name ('Sales'), a container ('Sales/Measures'), or a wildcard " +
                "('Sa*'); quote names with spaces (\"'Net Sales'\")",
            Arity = ArgumentArity.ZeroOrOne
        };

        var modelArgument = new Argument<string>("model")
        {
            Description = "Optional path to the model; defaults to the active connection",
            Arity = ArgumentArity.ZeroOrOne
        };

        var typeOption = new Option<string?>("--type")
        {
            Description = $"Only list objects of this kind, {TypeValidation.KindsHint}"
        };
        typeOption.Aliases.Add("-t");

        var pathsOnlyOption = new Option<bool>("--paths-only")
        {
            Description = "Print one object path per line, ready for piping"
        };

        var noMultilineOption = new Option<bool>("--no-multiline")
        {
            Description = "Show multi-line cell content (e.g. measure expressions) on one line, " +
                          "truncated. Applies to text output."
        };

        var command = new Command("ls", "List model objects")
        {
            pathArgument,
            modelArgument,
            typeOption,
            pathsOnlyOption,
            noMultilineOption
        };
        command.Aliases.Add("list");

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var invocation = new GetInvocation(
                "ls",
                Path: parseResult.GetValue(pathArgument),
                Mode: GetMode.List,
                TypeValue: parseResult.GetValue(typeOption),
                PathsOnly: parseResult.GetValue(pathsOnlyOption),
                NoMultiline: parseResult.GetValue(noMultilineOption));

            return await GetPipeline.RunAsync(
                parseResult,
                _providers,
                invocation,
                (string? firstValue, out ModelReference reference, out string? pathFilter, out int exitCode) =>
                    TryResolve(parseResult, firstValue, parseResult.GetValue(modelArgument), out reference, out pathFilter, out exitCode),
                cancellationToken);
        });

        return command;
    }

    /// <summary>
    /// Canonical order is <c>ls [path-filter] [model]</c>, matching <c>get</c>. The legacy
    /// <c>ls &lt;model&gt; [path-filter]</c> order stays accepted: a first positional that
    /// actually opens as a model is treated as the model.
    /// </summary>
    private bool TryResolve(
        ParseResult parseResult,
        string? firstValue,
        string? secondValue,
        out ModelReference reference,
        out string? pathFilter,
        out int exitCode)
    {
        var firstIsModel = !string.IsNullOrWhiteSpace(firstValue)
            && _providers.Any(p => p.CanOpen(new ModelReference(firstValue)));

        // Resolved before --recent is applied, and passed to it: TryResolveModel rejects
        // --recent combined with an explicit model, and handing it only --model would let
        // the positional form slip past that guard -- `ls --recent 1 ./model` then exited 0
        // having silently ignored the --recent selection, while `-m ./model` was rejected.
        var positionalModel = firstIsModel ? firstValue : secondValue;
        // Blank-checked rather than ?? : the guard downstream tests IsNullOrWhiteSpace, so a
        // present-but-empty --model would otherwise win the coalesce and hide the positional.
        var explicitModel = GlobalOptions.ModelValue(parseResult) is { } m && !string.IsNullOrWhiteSpace(m)
            ? m
            : positionalModel;

        if (!RecentConnections.TryResolveModel(
                parseResult,
                explicitModel,
                _state,
                out var activeReference,
                out exitCode))
        {
            reference = null!;
            pathFilter = null;
            return false;
        }

        var hasContextModel = !string.IsNullOrWhiteSpace(activeReference.Value);

        // --database rides along: a positional endpoint rebuilt without it would open the
        // server and then fail to resolve a catalog, or silently pick the only one there.
        var database = parseResult.GetValue(GlobalOptions.Database);

        if (firstIsModel)
        {
            reference = new ModelReference(firstValue!, database);
            pathFilter = secondValue;
        }
        else if (!string.IsNullOrWhiteSpace(secondValue))
        {
            reference = new ModelReference(secondValue, database);
            pathFilter = firstValue;
        }
        else if (hasContextModel)
        {
            reference = activeReference;
            pathFilter = firstValue;
        }
        else
        {
            reference = new ModelReference(firstValue ?? "");
            pathFilter = null;
        }

        return true;
    }
}
