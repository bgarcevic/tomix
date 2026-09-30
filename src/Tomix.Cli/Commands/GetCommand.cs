using System.CommandLine;
using Tomix.App.Get;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

/// <summary>
/// <c>tx get</c>: the single read command. The path decides the shape: one object shows its
/// properties, a wildcard or container path lists every match, and the analysis flags
/// (<c>--deps</c>, <c>--unused</c>) and selection flags (<c>--ls</c>, <c>--where</c>) combine over
/// the same resolution. <c>ls</c> and <c>deps</c> are shortcuts into the same
/// <see cref="GetPipeline"/>.
/// </summary>
internal sealed class GetCommand : ICommandModule
{
    private static readonly string[] DepsDirections = ["upstream", "downstream", "both"];

    private readonly IReadOnlyList<IModelProvider> _providers;

    private readonly CliStateStore _state;

    public GetCommand(IReadOnlyList<IModelProvider> providers, CliStateStore state)
    {
        _providers = providers;
        _state = state;
    }

    public Command Build()
    {
        var pathArgument = new Argument<string?>("path")
        {
            Description =
                "One object shows its properties ('Sales/Amount'); a wildcard ('Sa*') or container " +
                "('Sales/Measures') lists every match (default: the tables)",
            Arity = ArgumentArity.ZeroOrOne
        };

        var modelArgument = new Argument<string>("model")
        {
            Description = "Optional path to the model; defaults to the active connection",
            Arity = ArgumentArity.ZeroOrOne
        };

        var queryOption = new Option<string?>("--query")
        {
            Description = "Read just one property of one object (for example --query expression)"
        };

        var allOption = new Option<bool>("--all")
        {
            Description = "List every property, including unset ones (text output; JSON and CSV always carry all)"
        };

        var typeOption = new Option<string?>("--type")
        {
            Description = "Object kind: picks one when a path matches several objects, or filters a list; " +
                          TypeValidation.KindsHint
        };
        typeOption.Aliases.Add("-t");

        var lsOption = new Option<bool>("--ls")
        {
            Description = "List the objects the path selects, as a compact table (same as tx ls)"
        };

        var whereOption = new Option<string[]>("--where")
        {
            Description = "Keep objects whose property matches Prop=Value, ignoring case; '*' is a wildcard. " +
                          "Repeat to combine filters.",
            AllowMultipleArgumentsPerToken = false
        };

        var depsOption = new Option<string?>("--deps")
        {
            Description = "Trace what one object uses and what uses it: upstream, downstream, or both (default: both)",
            Arity = ArgumentArity.ZeroOrOne
        };

        var deepOption = new Option<bool>("--deep")
        {
            Description = "With --deps: walk the dependency chain recursively"
        };

        var maxDepthOption = new Option<int>("--max-depth")
        {
            Description = "With --deps --deep: how deep to walk (default: 10)",
            DefaultValueFactory = _ => 10
        };
        maxDepthOption.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int>() < 1)
                result.AddError("--max-depth must be at least 1.");
        });

        var unusedOption = new Option<bool>("--unused")
        {
            Description = "List measures and columns that nothing depends on (whole model, no path)"
        };

        var hiddenOption = new Option<bool>("--hidden")
        {
            Description = "With --unused: only unused objects that are hidden"
        };

        var pathsOnlyOption = new Option<bool>("--paths-only")
        {
            Description = "List one object path per line, ready for piping"
        };

        var noMultilineOption = new Option<bool>("--no-multiline")
        {
            Description = "List multi-line cell content (e.g. measure expressions) on one line, truncated. " +
                          "Applies to text output."
        };

        var command = new Command("get", "Read model objects and their properties")
        {
            pathArgument,
            modelArgument,
            queryOption,
            typeOption,
            allOption,
            lsOption,
            whereOption,
            depsOption,
            deepOption,
            maxDepthOption,
            unusedOption,
            hiddenOption,
            pathsOnlyOption,
            noMultilineOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            if (QuietCollisionGuard.TryReject(parseResult))
                return 2;

            var errorFormat = GlobalOptions.ErrorFormatValue(parseResult, GlobalOptions.OutputFormatValue(parseResult));
            var path = parseResult.GetValue(pathArgument);
            var model = parseResult.GetValue(modelArgument);

            // `--deps` takes an optional value, so `tx get --deps Sales/Amount` binds the path to
            // it. When no path was given and the value is not a direction, it is the path.
            var depsGiven = parseResult.GetResult(depsOption) is not null;
            var depsValue = parseResult.GetValue(depsOption);
            var direction = DepsDirection.Both;
            if (depsGiven && !string.IsNullOrWhiteSpace(depsValue))
            {
                if (DepsDirections.Contains(depsValue, StringComparer.OrdinalIgnoreCase))
                    direction = Enum.Parse<DepsDirection>(depsValue, ignoreCase: true);
                else if (string.IsNullOrWhiteSpace(path))
                    path = depsValue;
                else
                    return GetPipeline.UsageError(
                        errorFormat,
                        "TOMIX_USAGE",
                        $"Invalid --deps value '{depsValue}'.",
                        "Use --deps, --deps upstream, or --deps downstream.");
            }

            // With the path optional, a lone positional that opens as a model is the model:
            // `tx get ./model` lists its tables, as `tx ls ./model` does.
            if (string.IsNullOrWhiteSpace(model)
                && !string.IsNullOrWhiteSpace(path)
                && _providers.Any(p => p.CanOpen(new ModelReference(path))))
            {
                model = path;
                path = null;
            }

            var unused = parseResult.GetValue(unusedOption);
            var where = parseResult.GetValue(whereOption) ?? [];
            var listing = parseResult.GetValue(lsOption)
                          || where.Length > 0
                          || parseResult.GetValue(pathsOnlyOption)
                          || parseResult.GetValue(noMultilineOption);

            if (Conflict(parseResult, depsGiven, unused, listing,
                    queryOption, allOption, deepOption, maxDepthOption, hiddenOption) is { } conflict)
                return GetPipeline.UsageError(errorFormat, "TOMIX_USAGE", conflict, $"Run '{UsageErrors.HelpCommand(command)}' for usage.");

            var invocation = new GetInvocation(
                "get",
                path,
                Mode: unused ? GetMode.Unused : depsGiven ? GetMode.Deps : listing ? GetMode.List : GetMode.Auto,
                Query: parseResult.GetValue(queryOption),
                TypeValue: parseResult.GetValue(typeOption),
                All: parseResult.GetValue(allOption),
                Where: where,
                Direction: direction,
                Deep: parseResult.GetValue(deepOption),
                MaxDepth: parseResult.GetValue(maxDepthOption),
                HiddenOnly: parseResult.GetValue(hiddenOption),
                PathsOnly: parseResult.GetValue(pathsOnlyOption),
                NoMultiline: parseResult.GetValue(noMultilineOption));

            return await GetPipeline.RunAsync(
                parseResult,
                _providers,
                invocation,
                (string? p, out ModelReference reference, out string? resolvedPath, out int exitCode) =>
                {
                    resolvedPath = p;
                    var ok = RecentConnections.TryResolveModel(
                        parseResult, GlobalOptions.ModelValue(parseResult) ?? model, _state, out var resolved, out exitCode);
                    reference = resolved!;
                    return ok;
                },
                cancellationToken);
        });

        return command;
    }

    /// <summary>The first option combination that cannot mean anything, or null.</summary>
    private static string? Conflict(
        ParseResult parseResult,
        bool deps,
        bool unused,
        bool listing,
        Option<string?> query,
        Option<bool> all,
        Option<bool> deep,
        Option<int> maxDepth,
        Option<bool> hidden)
    {
        bool Given(Option option) => parseResult.GetResult(option) is { Implicit: false };

        if (deps && unused)
            return "--deps traces one object and --unused scans the whole model; use one of them.";
        if ((deps || unused) && listing)
            return "--ls, --where, --paths-only and --no-multiline list objects; they do not combine with --deps or --unused.";
        if ((deps || unused) && (Given(query) || Given(all)))
            return "--query and --all read properties; they do not combine with --deps or --unused.";
        if (!deps && (Given(deep) || Given(maxDepth)))
            return "--deep and --max-depth only apply with --deps.";
        if (!unused && Given(hidden))
            return "--hidden only applies with --unused.";
        if (listing && Given(all))
            return "--all shows one object's properties; it does not apply to a list.";
        return null;
    }
}
