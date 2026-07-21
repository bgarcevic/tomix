using System.CommandLine;
using Tomix.App.Bpa;
using Tomix.App.Mutations;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Bpa;
using Tomix.Core.Models;

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
        var command = new Command("bpa", "Best Practice Analyzer: run rules and manage rule collections");
        command.Subcommands.Add(BuildRulesCommand());
        command.Subcommands.Add(BuildRunCommand());
        return command;
    }

    private Command BuildRunCommand()
    {
        var modelArgument = new Argument<string>("model")
        {
            Description = "Path to model (if not using --model)",
            Arity = ArgumentArity.ZeroOrOne
        };

        var rulesOption = new Option<string[]>("--rules", "-r")
        {
            Description = "Path(s) or URL(s) to BPA rule file(s) in JSON format",
            AllowMultipleArgumentsPerToken = true
        };

        var rulesetOption = new Option<string?>("--ruleset")
        {
            Description = $"Standard BPA ruleset to use ({string.Join(", ", BpaRuleLoader.KnownRulesets)})"
        };

        var noModelRulesOption = new Option<bool>("--no-model-rules")
        {
            Description = "Exclude BPA rules embedded in the model's annotations"
        };

        var noDefaultsOption = new Option<bool>("--no-defaults")
        {
            Description = "Exclude the selected standard BPA ruleset"
        };

        var failOnOption = new Option<string?>("--fail-on")
        {
            Description = "Failure threshold: error (default) or warning"
        };

        var fixOption = new Option<bool>("--fix")
        {
            Description = "Apply fix expressions to auto-fix violations where possible"
        };

        var allowDeleteOption = new Option<bool>("--allow-delete")
        {
            Description = "With --fix: also apply destructive Delete() fixes that remove model objects"
        };

        var saveOption = new Option<bool>("--save")
        {
            Description = "Save model back to source after applying fixes"
        };

        var saveToOption = new Option<string?>("--save-to")
        {
            Description = "Save model to a different path after applying fixes"
        };

        var serializationOption = new Option<string?>("--serialization")
        {
            Description = "Model serialization: tmdl, bim (tmsl and auto also accepted)"
        };
        serializationOption.AcceptAmongIgnoreCase("tmdl", "bim", "tmsl", "auto");

        var stageOption = new Option<bool>("--stage")
        {
            Description = "Stage this command's mutation"
        };

        var revertOption = new Option<bool>("--revert")
        {
            Description = "Revert a staged mutation"
        };

        var noSyncOption = new Option<bool>("--no-sync")
        {
            Description = "Skip workspace sync when workspace mode is active."
        };

        var ruleOption = new Option<string[]>("--rule")
        {
            Description = "Run only specific rule(s) by ID",
            AllowMultipleArgumentsPerToken = true
        };

        var ciOption = new Option<string?>("--ci")
        {
            Description = "Emit CI logging commands to stderr: vsts or github"
        };

        var trxOption = new Option<string?>("--trx")
        {
            Description = "Write results as a VSTEST .trx file to the specified path"
        };

        var allowExternalRulesOption = new Option<bool>("--allow-external-rules")
        {
            Description = "Allow fetching BPA rule files from URLs embedded in model annotations"
        };

        var pathOption = new Option<string?>("--path")
        {
            Description = "Limit analysis to matched objects (literal names, wildcards, or paths)"
        };

        var noMultilineOption = new Option<bool>("--no-multiline")
        {
            Description = "Collapse each rule's guidance to a single line"
        };

        var detailsOption = new Option<bool>("--details")
        {
            Description = "Show full guidance and affected objects per rule (default is a compact list)"
        };

        var fullOption = new Option<bool>("--full")
        {
            Description = "Detail view listing every affected object (implies --details)"
        };

        var errorsOption = new Option<bool>("--errors")
        {
            Description = "Show only error-severity rules (combinable with --warnings/--info)"
        };

        var warningsOption = new Option<bool>("--warnings")
        {
            Description = "Show only warning-severity rules (combinable with --errors/--info)"
        };

        var infoOption = new Option<bool>("--info")
        {
            Description = "Show only info-severity rules (combinable with --errors/--warnings)"
        };

        var runCommand = new Command("run", "Run BPA rules against a model (--fix to auto-fix)")
        {
            modelArgument,
            rulesOption,
            rulesetOption,
            noModelRulesOption,
            noDefaultsOption,
            failOnOption,
            fixOption,
            allowDeleteOption,
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
                        Force: false,
                        parseResult.GetValue(noModelRulesOption),
                        parseResult.GetValue(allowExternalRulesOption),
                        parseResult.GetValue(stageOption),
                        parseResult.GetValue(revertOption),
                        NoSync: parseResult.GetValue(noSyncOption)),
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
                Info: parseResult.GetValue(infoOption));

            return CommandOutput.Render(
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
            Description = "Suppress built-in rules from output"
        };

        var ignoredOption = new Option<bool>("--ignored")
        {
            Description = "Show only ignored rules"
        };

        var disabledOption = new Option<bool>("--disabled")
        {
            Description = "Show only disabled rules"
        };

        var allOption = new Option<bool>("--all")
        {
            Description = "Show all rules including disabled and ignored"
        };

        var noMultilineOption = new Option<bool>("--no-multiline")
        {
            Description = "Collapse multi-line cell content in text output"
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

        var listCommand = new Command("list", "List BPA rules from all sources with status")
        {
            modelArgument,
            rulesetOption,
            noDefaultsOption,
            ignoredOption,
            disabledOption,
            noMultilineOption,
            allOption
        };

        listCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, format, "bpa rules list", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var modelPath = GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument);
            ModelReference? model = null;
            if (GlobalOptions.RecentSpecified(parseResult))
            {
                if (!RecentConnections.TryGetSource(parseResult, modelPath, _state, out var source, out var recentExit))
                    return recentExit;
                model = RecentConnections.CreateResolver(source, _state).ResolveReference(source.Model, source.Database, source.Server);
            }
            else if (!string.IsNullOrWhiteSpace(modelPath))
            {
                model = new ActiveModelResolver(_state).ResolveReference(
                    modelPath,
                    parseResult.GetValue(GlobalOptions.Database),
                    parseResult.GetValue(GlobalOptions.Server));
            }

            var result = await new BpaRulesListHandler(_providers, _bpaRules, _httpClient).HandleAsync(
                new BpaRulesListRequest(
                    Model: model,
                    All: parseResult.GetValue(allOption),
                    RulesFile: parseResult.GetValue(rulesFileOption),
                    Ruleset: parseResult.GetValue(rulesetOption),
                    NoDefaults: parseResult.GetValue(noDefaultsOption),
                    IgnoredOnly: parseResult.GetValue(ignoredOption),
                    DisabledOnly: parseResult.GetValue(disabledOption)),
                cancellationToken);

            return CommandOutput.Render(
                result,
                format,
                BpaRulesRenderer.RenderList,
                BpaRulesRenderer.ToListJson);
        });

        rulesCommand.Subcommands.Add(BuildRulesFlagCommand("disable", "Disable a built-in BPA rule for the current user"));
        rulesCommand.Subcommands.Add(BuildRulesFlagCommand("enable", "Re-enable a previously disabled built-in BPA rule"));
        rulesCommand.Subcommands.Add(BuildRulesIgnoreCommand("ignore", "Add a rule to the model's ignore list", ignore: true));
        rulesCommand.Subcommands.Add(listCommand);
        rulesCommand.Subcommands.Add(BuildRulesIgnoreCommand("unignore", "Remove a rule from the model's ignore list", ignore: false));
        return rulesCommand;
    }

    private Command BuildRulesFlagCommand(string name, string description)
    {
        var ruleIdArgument = new Argument<string>("rule-id") { Description = "Rule ID" };
        var command = new Command(name, description) { ruleIdArgument };
        var disable = name.Equals("disable", StringComparison.OrdinalIgnoreCase);

        command.SetAction(parseResult =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, format, $"bpa rules {name}", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var result = new BpaRulesDisableHandler(_bpaRules).Handle(
                new BpaRulesDisableRequest(parseResult.GetValue(ruleIdArgument)!, Disable: disable));

            return CommandOutput.Render(result, format, BpaRulesRenderer.RenderDisable, BpaRulesRenderer.ToDisableJson);
        });

        return command;
    }

    private Command BuildRulesIgnoreCommand(string name, string description, bool ignore)
    {
        var ruleIdArgument = new Argument<string>("rule-id") { Description = "Rule ID" };
        var modelArgument = OptionalModelArgument();
        var saveOption = new Option<bool>("--save")
        {
            Description = "Persist this command's mutation to the source location. Mutually exclusive with --revert and --stage."
        };
        var saveToOption = new Option<string?>("--save-to")
        {
            Description = "Save model to a different path"
        };
        var serializationOption = new Option<string?>("--serialization")
        {
            Description = "Model serialization when saving: tmdl, bim (tmsl and auto also accepted)"
        };
        serializationOption.AcceptAmongIgnoreCase("tmdl", "bim", "tmsl", "auto");
        var stageOption = new Option<bool>("--stage")
        {
            Description = "Stage this command's mutation"
        };
        var revertOption = new Option<bool>("--revert")
        {
            Description = "Revert a staged mutation"
        };
        var noSyncOption = new Option<bool>("--no-sync")
        {
            Description = "Skip workspace sync when workspace mode is active."
        };

        var command = new Command(name, description)
        {
            ruleIdArgument,
            modelArgument,
            saveOption,
            saveToOption,
            serializationOption,
            stageOption,
            revertOption,
            noSyncOption
        };

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

            var result = await new BpaRulesIgnoreHandler(_providers, _mutations).HandleAsync(
                new BpaRulesIgnoreRequest(
                    model,
                    parseResult.GetValue(ruleIdArgument)!,
                    Ignore: ignore,
                    Save: parseResult.GetValue(saveOption),
                    SaveTo: parseResult.GetValue(saveToOption),
                    Serialization: parseResult.GetValue(serializationOption) ?? "",
                    Stage: parseResult.GetValue(stageOption),
                    Revert: parseResult.GetValue(revertOption),
                    NoSync: parseResult.GetValue(noSyncOption)),
                cancellationToken);

            return CommandOutput.Render(result, format, BpaRulesRenderer.RenderIgnore, BpaRulesRenderer.ToIgnoreJson);
        });

        return command;
    }

    private static Argument<string?> OptionalModelArgument()
        => new("model")
        {
            Description = "Path to model",
            Arity = ArgumentArity.ZeroOrOne
        };
}
