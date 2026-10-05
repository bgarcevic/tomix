using System.CommandLine;
using Tomix.App.Get;
using Tomix.App.Models;
using Tomix.App.State;
using Tomix.Cli.Interactive;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

internal sealed class DepsCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly SessionScope? _session;

    private readonly CliStateStore _state;

    public DepsCommand(IReadOnlyList<IModelProvider> providers, CliStateStore state, SessionScope? session = null)
    {
        _session = session;
        _providers = providers;
        _state = state;
    }

    public Command Build()
    {
        var pathArgument = new Argument<string>("path")
        {
            Description = "The object to trace, slash-separated: 'Sales/Revenue'. DAX form works too.",
            Arity = ArgumentArity.ZeroOrOne
        };
        var modelArgument = new Argument<string>("model")
        {
            Description = "Optional path to the model; defaults to the active connection",
            Arity = ArgumentArity.ZeroOrOne
        };
        var upstreamOption = new Option<bool>("--upstream")
        {
            Description = "Trace only what this object uses"
        };
        var downstreamOption = new Option<bool>("--downstream")
        {
            Description = "Trace only what uses this object"
        };
        var deepOption = new Option<bool>("--deep")
        {
            Description = "Walk the dependency chain recursively"
        };
        var unusedOption = new Option<bool>("--unused")
        {
            Description = "List measures and columns that nothing depends on"
        };
        var hiddenOption = new Option<bool>("--hidden")
        {
            Description = "With --unused: restrict the list to unused objects that are hidden"
        };
        var maxDepthOption = new Option<int>("--max-depth")
        {
            Description = "How deep --deep walks (default: 10)",
            DefaultValueFactory = _ => 10
        };
        maxDepthOption.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int>() < 1)
                result.AddError("--max-depth must be at least 1.");
        });
        var typeOption = new Option<string?>("--type")
        {
            Description = "Type to pick when the path matches several objects under a table"
        };
        typeOption.Aliases.Add("-t");

        var command = new Command("deps", "Trace what an object uses and what uses it")
        {
            pathArgument,
            modelArgument,
            upstreamOption,
            downstreamOption,
            deepOption,
            unusedOption,
            hiddenOption,
            maxDepthOption,
            typeOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var upstream = parseResult.GetValue(upstreamOption);
            var downstream = parseResult.GetValue(downstreamOption);
            var unused = parseResult.GetValue(unusedOption);

            var invocation = new GetInvocation(
                "deps",
                parseResult.GetValue(pathArgument),
                Mode: unused ? GetMode.Unused : GetMode.Deps,
                TypeValue: parseResult.GetValue(typeOption),
                Direction: upstream == downstream ? DepsDirection.Both
                    : upstream ? DepsDirection.Upstream : DepsDirection.Downstream,
                Deep: parseResult.GetValue(deepOption),
                MaxDepth: parseResult.GetValue(maxDepthOption),
                HiddenOnly: parseResult.GetValue(hiddenOption));

            return await GetPipeline.RunAsync(
                parseResult,
                SessionScope.SourceFor(_session, _providers),
                invocation,
                (string? path, out ModelReference reference, out string? resolvedPath, out int exitCode) =>
                {
                    resolvedPath = path;
                    var ok = SessionScope.TryResolveModel(
                        _session,
                        parseResult,
                        GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument),
                        _state,
                        out var resolved,
                        out exitCode);
                    reference = resolved!;
                    return ok;
                },
                cancellationToken);
        });

        return command;
    }

}
