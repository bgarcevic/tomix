using System.CommandLine;
using Tomix.App.Bpa;
using Tomix.App.Mutations;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Bpa;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.Cli.Commands;

internal sealed class BpaCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    private readonly CliStateStore _state;
    private readonly MutationStores _mutations;
    private readonly BpaUserRuleState _bpaRules;
    private readonly string _configDirectory;
    private readonly HttpClient? _httpClient;

    public BpaCommand(
        IReadOnlyList<IModelProvider> providers,
        CliStateStore state,
        MutationStores mutations,
        BpaUserRuleState bpaRules,
        string configDirectory,
        HttpClient? httpClient = null)
    {
        _providers = providers;
        _state = state;
        _mutations = mutations;
        _bpaRules = bpaRules;
        _configDirectory = configDirectory;
        _httpClient = httpClient;
    }

    public Command Build()
    {
        var command = new Command("bpa", "Run best-practice rules and manage rule collections");
        command.Subcommands.Add(BuildRulesCommand());
        command.Subcommands.Add(BuildRunCommand());
        return command;
    }

    private Command BuildRunCommand()
    {
        var modelArgument = new Argument<string>("model")
        {
            Description = "Optional path to the model; defaults to the active connection",
            Arity = ArgumentArity.ZeroOrOne
        };

        var rulesOption = new Option<string[]>("--rules", "-r")
        {
            Description = "BPA rule files or URLs (JSON). Repeatable.",
            AllowMultipleArgumentsPerToken = true
        }.In("Rule options");

        var rulesetOption = new Option<string?>("--ruleset")
        {
            Description = $"Standard BPA ruleset to use ({string.Join(", ", BpaRuleLoader.KnownRulesets)})"
        }.In("Rule options");

        var noModelRulesOption = new Option<bool>("--no-model-rules")
        {
            Description = "Skip rules embedded in the model's annotations"
        }.In("Rule options");

        var noDefaultsOption = new Option<bool>("--no-defaults")
        {
            Description = "Exclude the selected standard BPA ruleset"
        }.In("Rule options");

        var failOnOption = new Option<string?>("--fail-on")
        {
            Description = "Failure threshold: error or warning (default: error). Rules that cannot be evaluated count as error-severity findings."
        }.In("CI options");

        var fixOption = new Option<bool>("--fix")
        {
            Description = "Fix violations whose rules provide a fix expression"
        };

        var allowDeleteOption = new Option<bool>("--allow-delete")
        {
            Description = "With --fix: also apply destructive Delete() fixes that remove model objects"
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "With --fix: list each fix as \"Would fix:\" with before/after values, and apply, save, or stage nothing"
        };

        var forceOption = LifecycleOptions.Force();
        var overwriteOption = LifecycleOptions.Overwrite();

        var saveOption = LifecycleOptions.Save();

        var saveToOption = LifecycleOptions.SaveTo();

        var serializationOption = LifecycleOptions.Serialization();

        var stageOption = LifecycleOptions.Stage();

        var revertOption = LifecycleOptions.Revert();

        var noSyncOption = LifecycleOptions.NoSync();

        var ruleOption = new Option<string[]>("--rule")
        {
            Description = "Run only this rule. Repeatable.",
            AllowMultipleArgumentsPerToken = true
        }.In("Rule options");

        var ciOption = new Option<string?>("--ci")
        {
            Description = "Print CI log-group commands to stderr for the given system: vsts or github"
        }.In("CI options");

        var trxOption = new Option<string?>("--trx")
        {
            Description = "Write results to a .trx test-run file at this path"
        }.In("CI options");

        var allowExternalRulesOption = new Option<bool>("--allow-external-rules")
        {
            Description = "Allow rule URLs found in model annotations to be fetched"
        }.In("Rule options");

        var pathOption = new Option<string?>("--path")
        {
            Description = "Limit analysis to matched objects (literal names, wildcards, or paths)"
        }.In("Display options");

        var noMultilineOption = new Option<bool>("--no-multiline")
        {
            Description = "Show each rule's guidance on one line"
        }.In("Display options");

        var detailsOption = new Option<bool>("--details")
        {
            Description = "Show full guidance and affected objects per rule instead of a compact list"
        }.In("Display options");

        var fullOption = new Option<bool>("--full")
        {
            Description = "Detail view listing every affected object (implies --details)"
        }.In("Display options");

        var errorsOption = new Option<bool>("--errors")
        {
            Description = "Show only error-severity rules (combinable with --warnings/--info)"
        }.In("Display options");

        var warningsOption = new Option<bool>("--warnings")
        {
            Description = "Show only warning-severity rules (combinable with --errors/--info)"
        }.In("Display options");

        var infoOption = new Option<bool>("--info")
        {
            Description = "Show only info-severity rules (combinable with --errors/--warnings)"
        }.In("Display options");

        var runCommand = new Command("run", "Run best-practice rules against a model")
        {
            modelArgument,
            rulesOption,
            rulesetOption,
            noModelRulesOption,
            noDefaultsOption,
            failOnOption,
            fixOption,
            allowDeleteOption,
            dryRunOption,
            forceOption,
            overwriteOption,
            saveOption,
            saveToOption,
            serializationOption,
            ruleOption,
            ciOption,
            trxOption,
            allowExternalRulesOption,
            pathOption,
            noMultilineOption,
            detailsOption,
            fullOption,
            errorsOption,
            warningsOption,
            infoOption,
            stageOption,
            revertOption,
            noSyncOption
        };

        runCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            if (!CommandOutput.TryValidateFormat(parseResult, format, "bpa run", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var ruleFiles = parseResult.GetValue(rulesOption);
            var ruleIds = parseResult.GetValue(ruleOption);

            if (!RecentConnections.TryResolveModel(
                    parseResult,
                    GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument),
                    _state,
                    out var model,
                    out var recentExit))
                return recentExit;

            // --allow-delete opts into Delete() fixes that remove model objects, and --revert
            // drops staged work; both confirm before running. Plain --fix applies property
            // fixes only and asks nothing unless it persists (--save/--stage paths gate there).
            // --dry-run discards every fix, so a destructive preview needs no confirmation.
            var dryRun = parseResult.GetValue(dryRunOption);
            if (parseResult.GetValue(revertOption))
            {
                if (!ConfirmationHelper.ConfirmOrAbort(
                        "Revert staged changes", $"for {model.Value}", parseResult, format))
                    return 1;
            }
            else if (parseResult.GetValue(fixOption) && parseResult.GetValue(allowDeleteOption) && !dryRun
                && !ConfirmationHelper.ConfirmOrAbort(
                    "Apply destructive BPA fixes", $"on {model.Value}", parseResult, format))
                return 1;

            var result = await CliSpinner.RunAsync(
                "Running BPA analysis...",
                () => new BpaRunHandler(
                    _providers, _mutations, _bpaRules, _configDirectory, _httpClient).HandleAsync(
                    new BpaRunRequest(
                        model,
                        ruleFiles,
                        parseResult.GetValue(noDefaultsOption),
                        parseResult.GetValue(pathOption),
                        ruleIds,
                        parseResult.GetValue(fixOption),
                        parseResult.GetValue(allowDeleteOption),
                        parseResult.GetValue(rulesetOption),
                        parseResult.GetValue(failOnOption),
                        parseResult.GetValue(saveOption),
                        parseResult.GetValue(saveToOption),
                        parseResult.GetValue(serializationOption) ?? "",
                        Force: parseResult.GetValue(forceOption),
                        parseResult.GetValue(noModelRulesOption),
                        parseResult.GetValue(allowExternalRulesOption),
                        parseResult.GetValue(stageOption),
                        parseResult.GetValue(revertOption),
                        NoSync: parseResult.GetValue(noSyncOption),
                        Overwrite: parseResult.GetValue(overwriteOption),
                        DryRun: dryRun),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(format) || OutputFormats.IsCsv(format));

            var ci = parseResult.GetValue(ciOption);

            if (result.Data is not null)
            {
                var trx = parseResult.GetValue(trxOption);
                if (!string.IsNullOrWhiteSpace(trx))
                    TrxWriter.Write(trx, "tx bpa run", BpaRunRenderer.ToTrxTests(result.Data));

                BpaRunRenderer.EmitCi(ci, result.Data.Violations);
            }

            if (!string.IsNullOrWhiteSpace(ci))
                return result.ExitCode;

            var full = parseResult.GetValue(fullOption);
            var ruleScoped = ruleIds is { Length: > 0 };
            var view = new BpaRunView.RunOptions(
                NoMultiline: parseResult.GetValue(noMultilineOption),
                Full: full,
                Details: parseResult.GetValue(detailsOption) || full || ruleScoped,
                Errors: parseResult.GetValue(errorsOption),
                Warnings: parseResult.GetValue(warningsOption),
                Info: parseResult.GetValue(infoOption),
                CommandTokens: parseResult.Tokens.Select(t => t.Value).ToList(),
                // Statistics come from a live engine: the model itself, or the remote mirror of a
                // workspace-mode local primary. A plain file has neither, so the hint differs.
                CanCollectVertipaqStats: model.IsRemote
                    || new ActiveModelResolver(_state).ResolveSyncTarget(model) is { IsRemote: true });

            return CommandOutput.Render(
                parseResult,
                result,
                format,
                data => BpaRunRenderer.Render(data, view),
                BpaRunRenderer.ToJson);
        });

        return runCommand;
    }

    private Command BuildRulesCommand()
    {
        var rulesFileOption = new Option<string?>("--rules-file")
        {
            Description = "Path to a BPA rules JSON file"
        };

        var rulesetOption = new Option<string?>("--ruleset")
        {
            Description = $"Standard BPA ruleset to use ({string.Join(", ", BpaRuleLoader.KnownRulesets)})"
        };

        var noDefaultsOption = new Option<bool>("--no-defaults")
        {
            Description = "Leave the built-in ruleset out of the listing"
        };

        var ignoredOption = new Option<bool>("--ignored")
        {
            Description = "List only rules on the model's ignore list"
        };

        var disabledOption = new Option<bool>("--disabled")
        {
            Description = "List only rules disabled for this user"
        };

        var allOption = new Option<bool>("--all")
        {
            Description = "Include disabled and ignored rules in the listing"
        };

        var modelArgument = new Argument<string>("model")
        {
            Description = "Path to model",
            Arity = ArgumentArity.ZeroOrOne
        };

        var rulesCommand = new Command("rules", "Manage BPA rule collections")
        {
            rulesFileOption
        };

        var listCommand = new Command("list", "List rules from every source, with each rule's status")
        {
            modelArgument,
            rulesetOption,
            noDefaultsOption,
            ignoredOption,
            disabledOption,
            allOption
        };

        listCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, format, "bpa rules list", OutputFormats.Text, OutputFormats.Json))
                return 2;

            if (!TryResolveOptionalModel(parseResult, parseResult.GetValue(modelArgument), out var model, out var modelExit))
                return modelExit;

            var request = new BpaRulesListRequest(
                Model: model,
                All: parseResult.GetValue(allOption),
                RulesFile: parseResult.GetValue(rulesFileOption),
                Ruleset: parseResult.GetValue(rulesetOption),
                NoDefaults: parseResult.GetValue(noDefaultsOption),
                IgnoredOnly: parseResult.GetValue(ignoredOption),
                DisabledOnly: parseResult.GetValue(disabledOption));
            var result = await new BpaRulesListHandler(_providers, _bpaRules, _httpClient, _configDirectory).HandleAsync(
                request, cancellationToken);

            return CommandOutput.Render(
                parseResult,
                result,
                format,
                value => BpaRulesRenderer.RenderList(value, request),
                BpaRulesRenderer.ToListJson);
        });

        rulesCommand.Subcommands.Add(BuildRulesAddCommand(rulesFileOption));
        rulesCommand.Subcommands.Add(BuildRulesFlagCommand("disable", "Turn off a built-in rule for this user", rulesFileOption));
        rulesCommand.Subcommands.Add(BuildRulesFlagCommand("enable", "Turn a disabled built-in rule back on", rulesFileOption));
        rulesCommand.Subcommands.Add(BuildRulesIgnoreCommand("ignore", "Put a rule on the model's ignore list", ignore: true, rulesFileOption));
        rulesCommand.Subcommands.Add(BuildRulesInitCommand(rulesFileOption));
        rulesCommand.Subcommands.Add(listCommand);
        rulesCommand.Subcommands.Add(BuildRulesRemoveCommand(rulesFileOption));
        rulesCommand.Subcommands.Add(BuildRulesSetCommand(rulesFileOption));
        rulesCommand.Subcommands.Add(BuildRulesShowCommand(rulesFileOption));
        rulesCommand.Subcommands.Add(BuildRulesIgnoreCommand("unignore", "Take a rule off the model's ignore list", ignore: false, rulesFileOption));
        return rulesCommand;
    }

    private Command BuildRulesShowCommand(Option<string?> rulesFileOption)
    {
        var ruleIdArgument = new Argument<string>("rule-id") { Description = "Rule ID" };
        var modelArgument = OptionalModelArgument();
        var rulesetOption = new Option<string?>("--ruleset")
        {
            Description = $"Standard BPA ruleset to look in ({string.Join(", ", BpaRuleLoader.KnownRulesets)})"
        };
        var noDefaultsOption = new Option<bool>("--no-defaults")
        {
            Description = "Leave the built-in ruleset out of the lookup"
        };

        var command = new Command("show", "Show a rule's description, scope, expression, and fix")
        {
            ruleIdArgument,
            modelArgument,
            rulesetOption,
            noDefaultsOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, format, "bpa rules show", OutputFormats.Text, OutputFormats.Json))
                return 2;

            if (!TryResolveOptionalModel(parseResult, parseResult.GetValue(modelArgument), out var model, out var modelExit))
                return modelExit;

            var result = await new BpaRulesListHandler(_providers, _bpaRules, _httpClient, _configDirectory).HandleAsync(
                new BpaRulesListRequest(
                    Model: model,
                    RulesFile: parseResult.GetValue(rulesFileOption),
                    Ruleset: parseResult.GetValue(rulesetOption),
                    NoDefaults: parseResult.GetValue(noDefaultsOption),
                    RuleId: parseResult.GetValue(ruleIdArgument)),
                cancellationToken);

            return CommandOutput.Render(
                parseResult,
                result,
                format,
                BpaRulesRenderer.RenderShow,
                BpaRulesRenderer.ToListJson);
        });

        return command;
    }

    /// <summary>
    /// The model for the read-only <c>bpa rules</c> commands: optional, so without one they
    /// list the rulesets alone; with <c>--recent</c> it comes from the recent list.
    /// </summary>
    private bool TryResolveOptionalModel(ParseResult parseResult, string? modelArgument, out ModelReference? model, out int exitCode)
    {
        model = null;
        exitCode = 0;
        var modelPath = GlobalOptions.ModelValue(parseResult) ?? modelArgument;
        if (GlobalOptions.RecentSpecified(parseResult))
        {
            if (!RecentConnections.TryGetSource(parseResult, modelPath, _state, out var source, out exitCode))
                return false;
            model = RecentConnections.CreateResolver(source, _state).ResolveReference(source.Model, source.Database, source.Server);
        }
        else if (!string.IsNullOrWhiteSpace(modelPath))
        {
            model = new ActiveModelResolver(_state).ResolveReference(
                modelPath,
                parseResult.GetValue(GlobalOptions.Database),
                parseResult.GetValue(GlobalOptions.Server));
        }

        return true;
    }

    private Command BuildRulesFlagCommand(string name, string description, Option<string?> rulesFileOption)
    {
        var ruleIdArgument = new Argument<string>("rule-id") { Description = "Rule ID" };
        var disable = name.Equals("disable", StringComparison.OrdinalIgnoreCase);
        var allowUnknownOption = AllowUnknownOption();
        var command = new Command(name, description) { ruleIdArgument };
        if (disable)
            command.Options.Add(allowUnknownOption);

        command.SetAction(parseResult =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, format, $"bpa rules {name}", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var result = new BpaRulesDisableHandler(_bpaRules, _configDirectory).Handle(
                new BpaRulesDisableRequest(
                    parseResult.GetValue(ruleIdArgument)!,
                    Disable: disable,
                    AllowUnknown: disable && parseResult.GetValue(allowUnknownOption),
                    RulesFile: parseResult.GetValue(rulesFileOption)));

            return CommandOutput.Render(parseResult, result, format, BpaRulesRenderer.RenderDisable, BpaRulesRenderer.ToDisableJson);
        });

        return command;
    }

    private Command BuildRulesIgnoreCommand(string name, string description, bool ignore, Option<string?> rulesFileOption)
    {
        var ruleIdArgument = new Argument<string>("rule-id") { Description = "Rule ID" };
        var modelArgument = OptionalModelArgument();
        var overwriteOption = LifecycleOptions.Overwrite();
        var saveOption = LifecycleOptions.Save();
        var saveToOption = LifecycleOptions.SaveTo();
        var serializationOption = LifecycleOptions.Serialization();
        var stageOption = LifecycleOptions.Stage();
        var revertOption = LifecycleOptions.Revert();
        var noSyncOption = LifecycleOptions.NoSync();
        var forceOption = LifecycleOptions.Force();
        var allowUnknownOption = AllowUnknownOption();

        var command = new Command(name, description)
        {
            ruleIdArgument,
            modelArgument,
            overwriteOption,
            saveOption,
            saveToOption,
            serializationOption,
            stageOption,
            revertOption,
            noSyncOption,
            forceOption
        };
        if (ignore)
            command.Options.Add(allowUnknownOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, format, $"bpa rules {name}", OutputFormats.Text, OutputFormats.Json))
                return 2;

            if (!RecentConnections.TryResolveModel(
                    parseResult,
                    GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument),
                    _state,
                    out var model,
                    out var recentExit))
                return recentExit;

            var result = await new BpaRulesIgnoreHandler(_providers, _mutations, _configDirectory).HandleAsync(
                new BpaRulesIgnoreRequest(
                    model,
                    parseResult.GetValue(ruleIdArgument)!,
                    Ignore: ignore,
                    Overwrite: parseResult.GetValue(overwriteOption),
                    Save: parseResult.GetValue(saveOption),
                    SaveTo: parseResult.GetValue(saveToOption),
                    Serialization: parseResult.GetValue(serializationOption) ?? "",
                    Stage: parseResult.GetValue(stageOption),
                    Revert: parseResult.GetValue(revertOption),
                    NoSync: parseResult.GetValue(noSyncOption),
                    Force: parseResult.GetValue(forceOption),
                    AllowUnknown: ignore && parseResult.GetValue(allowUnknownOption),
                    RulesFile: parseResult.GetValue(rulesFileOption)),
                cancellationToken);

            return CommandOutput.Render(parseResult, result, format, BpaRulesRenderer.RenderIgnore, BpaRulesRenderer.ToIgnoreJson);
        });

        return command;
    }

    /// <summary>The rule-field options shared by <c>bpa rules add</c> and <c>set</c>.</summary>
    private sealed class RuleFieldOptions
    {
        public Option<string?> Name { get; } = new("--name") { Description = "Rule name shown in results" };
        public Option<string?> Category { get; } = new("--category") { Description = $"Rule category (add default: {BpaRulesAddHandler.DefaultCategory})" };
        public Option<string?> Severity { get; } = new("--severity") { Description = "error, warning, or info (add default: warning)" };
        public Option<string?> Scope { get; } = new("--scope") { Description = "Object types the rule checks, comma-separated (for example \"Measure, CalculatedColumn\")" };
        public Option<string?> Expression { get; } = new("--expression") { Description = "Rule expression; an object that matches it is a violation" };
        public Option<string?> Description { get; } = new("--description") { Description = "Guidance shown with violations (\"\" removes it)" };
        public Option<string?> FixExpression { get; } = new("--fix-expression") { Description = "Fix applied by 'bpa run --fix' (\"\" removes it)" };

        public void AddTo(Command command)
        {
            foreach (var option in new[] { Name, Category, Severity, Scope, Expression, Description, FixExpression })
                command.Options.Add(option);
        }

        public BpaRuleFields Read(ParseResult parseResult) => new(
            parseResult.GetValue(Name),
            parseResult.GetValue(Category),
            parseResult.GetValue(Severity),
            parseResult.GetValue(Scope),
            parseResult.GetValue(Expression),
            parseResult.GetValue(Description),
            parseResult.GetValue(FixExpression));
    }

    private Command BuildRulesAddCommand(Option<string?> rulesFileOption)
    {
        var idOption = new Option<string>("--id") { Description = "Rule ID", Required = true };
        var fields = new RuleFieldOptions();
        var command = new Command("add", "Add a custom rule to a rules file") { idOption };
        fields.AddTo(command);

        command.SetAction(parseResult => RenderRulesFile(parseResult, "bpa rules add", () =>
            new BpaRulesAddHandler(_configDirectory).Handle(new BpaRulesAddRequest(
                parseResult.GetValue(idOption)!,
                fields.Read(parseResult),
                parseResult.GetValue(rulesFileOption)))));

        return command;
    }

    private Command BuildRulesSetCommand(Option<string?> rulesFileOption)
    {
        var ruleIdArgument = new Argument<string>("rule-id") { Description = "Rule ID" };
        var fields = new RuleFieldOptions();
        var command = new Command("set", "Change a custom rule in a rules file") { ruleIdArgument };
        fields.AddTo(command);

        command.SetAction(parseResult => RenderRulesFile(parseResult, "bpa rules set", () =>
            new BpaRulesSetHandler(_configDirectory).Handle(new BpaRulesSetRequest(
                parseResult.GetValue(ruleIdArgument)!,
                fields.Read(parseResult),
                parseResult.GetValue(rulesFileOption)))));

        return command;
    }

    private Command BuildRulesRemoveCommand(Option<string?> rulesFileOption)
    {
        var ruleIdArgument = new Argument<string>("rule-id") { Description = "Rule ID" };
        var command = new Command("remove", "Delete a custom rule from a rules file") { ruleIdArgument };

        command.SetAction(parseResult => RenderRulesFile(parseResult, "bpa rules remove", () =>
            new BpaRulesRemoveHandler(_configDirectory).Handle(new BpaRulesRemoveRequest(
                parseResult.GetValue(ruleIdArgument)!,
                parseResult.GetValue(rulesFileOption)))));

        return command;
    }

    private Command BuildRulesInitCommand(Option<string?> rulesFileOption)
    {
        var forceOption = new Option<bool>("--force") { Description = "Replace an existing rules file with an empty one" };
        var command = new Command("init", "Create an empty rules file") { forceOption };

        command.SetAction(parseResult => RenderRulesFile(parseResult, "bpa rules init", () =>
            new BpaRulesInitHandler(_configDirectory).Handle(new BpaRulesInitRequest(
                parseResult.GetValue(rulesFileOption),
                parseResult.GetValue(forceOption)))));

        return command;
    }

    /// <summary>Format check, then the shared add/set/remove/init rendering.</summary>
    private static int RenderRulesFile(
        ParseResult parseResult, string commandName, Func<TomixResult<BpaRulesFileResult>> handle)
    {
        var format = GlobalOptions.OutputFormatValue(parseResult);
        if (!CommandOutput.TryValidateFormat(parseResult, format, commandName, OutputFormats.Text, OutputFormats.Json))
            return 2;

        return CommandOutput.Render(parseResult, handle(), format, BpaRulesRenderer.RenderFile, BpaRulesRenderer.ToFileJson);
    }

    private static Option<bool> AllowUnknownOption()
        => new(BpaKnownRules.AllowUnknownOption)
        {
            Description = "Accept a rule ID that no loaded rule has (for example, one from a remote rule file)"
        };

    private static Argument<string?> OptionalModelArgument()
        => new("model")
        {
            Description = "Path to model",
            Arity = ArgumentArity.ZeroOrOne
        };
}
