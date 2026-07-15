using System.CommandLine;
using Tomix.App.Bpa;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Bpa;
using Tomix.Core.Models;
using Spectre.Console;

namespace Tomix.Cli.Commands;

internal sealed class BpaCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public BpaCommand(IReadOnlyList<IModelProvider> providers) => _providers = providers;

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

        var vpaxOption = new Option<string?>("--vpax")
        {
            Description = "Load VertiPaq Analyzer stats from a .vpax file to enable VPA-aware rules"
        };

        var vpaRulesOption = new Option<bool>("--vpa-rules")
        {
            Description = "Include built-in VPA-aware rules"
        };

        var fixOption = new Option<bool>("--fix")
        {
            Description = "Apply fix expressions to auto-fix violations where possible"
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
            vpaxOption,
            vpaRulesOption,
            failOnOption,
            fixOption,
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
            if (!CommandOutput.TryValidateFormat(format, "bpa run", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var ruleFiles = parseResult.GetValue(rulesOption);
            var ruleIds = parseResult.GetValue(ruleOption);

            if (!RecentConnections.TryGetSource(
                    parseResult,
                    GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument),
                    out var source,
                    out var recentExit))
                return recentExit;

            var result = await CliSpinner.RunAsync(
                "Running BPA analysis...",
                () => new BpaRunHandler(_providers).HandleAsync(
                    new BpaRunRequest(
                        RecentConnections.CreateResolver(source).ResolveReference(source.Model, source.Database, source.Server),
                        ruleFiles,
                        parseResult.GetValue(noDefaultsOption),
                        parseResult.GetValue(pathOption),
                        ruleIds,
                        parseResult.GetValue(fixOption),
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
                EmitCi(ci, result.Data.Violations);

            if (!string.IsNullOrWhiteSpace(ci))
                return result.ExitCode;

            var full = parseResult.GetValue(fullOption);
            var ruleScoped = ruleIds is { Length: > 0 };
            var view = new RunViewOptions(
                NoMultiline: parseResult.GetValue(noMultilineOption),
                Full: full,
                Details: parseResult.GetValue(detailsOption) || full || ruleScoped,
                Errors: parseResult.GetValue(errorsOption),
                Warnings: parseResult.GetValue(warningsOption),
                Info: parseResult.GetValue(infoOption));

            return CommandOutput.Render(
                result,
                format,
                data => RenderRun(data, view),
                ProjectRunJson);
        });

        return runCommand;
    }

    private Command BuildRulesCommand()
    {
        var rulesFileOption = new Option<string?>("--rules-file")
        {
            Description = "Path to a BPA rules JSON file"
        };

        var modelRulesOption = new Option<bool>("--model-rules")
        {
            Description = "Operate on rules embedded in the model annotation instead of a file"
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
            rulesFileOption,
            modelRulesOption
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
            if (!CommandOutput.TryValidateFormat(format, "bpa rules list", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var modelPath = GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument);
            ModelReference? model = null;
            if (GlobalOptions.RecentSpecified(parseResult))
            {
                if (!RecentConnections.TryGetSource(parseResult, modelPath, out var source, out var recentExit))
                    return recentExit;
                model = RecentConnections.CreateResolver(source).ResolveReference(source.Model, source.Database, source.Server);
            }
            else if (!string.IsNullOrWhiteSpace(modelPath))
            {
                model = new ActiveModelResolver().ResolveReference(
                    modelPath,
                    parseResult.GetValue(GlobalOptions.Database),
                    parseResult.GetValue(GlobalOptions.Server));
            }

            var result = await new BpaRulesListHandler(_providers).HandleAsync(
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
                RenderRulesList,
                ProjectRulesListJson);
        });

        rulesCommand.Subcommands.Add(BuildRulesAddCommand());
        rulesCommand.Subcommands.Add(BuildRulesFlagCommand("disable", "Disable a built-in BPA rule for the current user"));
        rulesCommand.Subcommands.Add(BuildRulesFlagCommand("enable", "Re-enable a previously disabled built-in BPA rule"));
        rulesCommand.Subcommands.Add(BuildRulesIgnoreCommand("ignore", "Add a rule to the model's ignore list", ignore: true));
        rulesCommand.Subcommands.Add(BuildRulesInitCommand());
        rulesCommand.Subcommands.Add(listCommand);
        rulesCommand.Subcommands.Add(BuildRulesRemoveCommand());
        rulesCommand.Subcommands.Add(BuildRulesSetCommand());
        rulesCommand.Subcommands.Add(BuildRulesIgnoreCommand("unignore", "Remove a rule from the model's ignore list", ignore: false));
        return rulesCommand;
    }

    private static Command BuildRulesAddCommand()
    {
        var command = new Command("add", "Add a new BPA rule")
        {
            new Argument<string>("id") { Description = "Rule ID" },
            OptionalModelArgument(),
            new Option<string?>("--name") { Description = "Rule display name" },
            new Option<string?>("--expression") { Description = "Dynamic LINQ expression" },
            new Option<string?>("--scope") { Description = "Comma-separated scopes" },
            new Option<string?>("--category") { Description = "Rule category" },
            new Option<string?>("--severity") { Description = "Severity: 1, 2, or 3" },
            new Option<string?>("--description") { Description = "Rule description" },
            new Option<string?>("--fix-expression") { Description = "Dynamic LINQ fix expression" },
            new Option<bool>("--save") { Description = "Save model after adding rule" }
        };
        command.SetAction(_ => UnsupportedRulesAction("add"));
        return command;
    }

    private static Command BuildRulesSetCommand()
    {
        var queryOption = new Option<string?>("-q")
        {
            Description = "Property: name, expression, scope, category, severity, description, fixExpression"
        };
        var valueOption = new Option<string?>("-i")
        {
            Description = "New value"
        };

        var command = new Command("set", "Update a BPA rule's properties")
        {
            new Argument<string>("rule-id") { Description = "Rule ID to update" },
            OptionalModelArgument(),
            queryOption,
            valueOption,
            new Option<bool>("--save") { Description = "Save model after updating rule" }
        };
        command.SetAction(_ => UnsupportedRulesAction("set"));
        return command;
    }

    private static Command BuildRulesRemoveCommand()
    {
        var command = new Command("rm", "Remove a BPA rule")
        {
            new Argument<string>("rule-id") { Description = "Rule ID to remove" },
            OptionalModelArgument(),
            new Option<bool>("--save") { Description = "Save model after removing rule" }
        };
        command.SetAction(_ => UnsupportedRulesAction("rm"));
        return command;
    }

    private static Command BuildRulesFlagCommand(string name, string description)
    {
        var ruleIdArgument = new Argument<string>("rule-id") { Description = "Rule ID" };
        var command = new Command(name, description) { ruleIdArgument };
        var disable = name.Equals("disable", StringComparison.OrdinalIgnoreCase);

        command.SetAction(parseResult =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(format, $"bpa rules {name}", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var result = new BpaRulesDisableHandler().Handle(
                new BpaRulesDisableRequest(parseResult.GetValue(ruleIdArgument)!, Disable: disable));

            return CommandOutput.Render(result, format, RenderRulesDisable, ProjectRulesDisableJson);
        });

        return command;
    }

    private static void RenderRulesDisable(BpaRulesDisableResult result)
    {
        if (!result.Changed)
        {
            AnsiConsole.MarkupLine(Styling.Muted(
                $"Rule '{Styling.MarkupEscape(result.RuleId)}' was already {(result.Disabled ? "disabled" : "enabled")} — no change."));
            return;
        }

        AnsiConsole.MarkupLine(result.Disabled
            ? $"Rule {Styling.Value(result.RuleId)} disabled for the current user."
            : $"Rule {Styling.Value(result.RuleId)} re-enabled for the current user.");
        AnsiConsole.MarkupLine($"  {Styling.KeyValue("Disabled rules:", result.DisabledRuleIds.Count.ToString())}");
    }

    private static object ProjectRulesDisableJson(BpaRulesDisableResult result)
        => new
        {
            ruleId = result.RuleId,
            disabled = result.Disabled,
            changed = result.Changed,
            disabledRuleIds = result.DisabledRuleIds
        };

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
            if (!CommandOutput.TryValidateFormat(format, $"bpa rules {name}", OutputFormats.Text, OutputFormats.Json))
                return 2;

            if (!RecentConnections.TryGetSource(
                    parseResult,
                    GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument),
                    out var source,
                    out var recentExit))
                return recentExit;
            var model = RecentConnections.CreateResolver(source).ResolveReference(source.Model, source.Database, source.Server);

            var result = await new BpaRulesIgnoreHandler(_providers).HandleAsync(
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

            return CommandOutput.Render(result, format, RenderRulesIgnore, ProjectRulesIgnoreJson);
        });

        return command;
    }

    private static void RenderRulesIgnore(BpaRulesIgnoreResult result)
    {
        var verb = result.Ignored ? "ignored" : "no longer ignored";

        if (!result.Changed)
        {
            AnsiConsole.MarkupLine(Styling.Muted(
                $"Rule '{Styling.MarkupEscape(result.RuleId)}' was already {(result.Ignored ? "ignored" : "not ignored")} — no change."));
            return;
        }

        AnsiConsole.MarkupLine($"Rule {Styling.Value(result.RuleId)} is now {verb} for {Styling.Value(result.ModelName)}.");
        AnsiConsole.MarkupLine($"  {Styling.KeyValue("Ignored rules:", result.RuleIds.Count.ToString())}");

        if (result.Saved is true or string)
            AnsiConsole.MarkupLine($"  {Styling.Success("Model saved.")}");
        else if (result.Staged == true)
            AnsiConsole.MarkupLine($"  {Styling.Success("Mutation staged.")}");
        else
            AnsiConsole.MarkupLine($"  {Styling.Muted("Not saved — re-run with --save to persist or --stage to stage.")}");

        if (result.Synced)
            AnsiConsole.MarkupLine($"  {Styling.Success($"Synced: {Styling.MarkupEscape(result.SyncTarget!)}")}");
        else if (result.SyncWarning is not null)
            AnsiConsole.MarkupLine($"  {Styling.Warning(Styling.MarkupEscape(result.SyncWarning))}");
    }

    private static object ProjectRulesIgnoreJson(BpaRulesIgnoreResult result)
        => new
        {
            ruleId = result.RuleId,
            ignored = result.Ignored,
            changed = result.Changed,
            ruleIds = result.RuleIds,
            saved = result.Saved,
            staged = result.Staged,
            model = result.ModelName
        };

    private static Command BuildRulesInitCommand()
    {
        var command = new Command("init", "Create an empty BPA rules file at the resolved path")
        {
            new Option<bool>("--force") { Description = "Overwrite an existing rules file" }
        };
        command.SetAction(_ => UnsupportedRulesAction("init"));
        return command;
    }

    private static Argument<string?> OptionalModelArgument()
        => new("model")
        {
            Description = "Path to model",
            Arity = ArgumentArity.ZeroOrOne
        };

    private static int UnsupportedRulesAction(string command)
    {
        Console.Error.WriteLine($"Command 'bpa rules {command}' is not implemented yet.");
        return 1;
    }

    private static object ProjectRunJson(BpaRunResult result)
        => new
        {
            rulesEvaluated = result.RulesEvaluated,
            violations = result.Violations.Count,
            ruleErrors = result.RuleErrors,
            ignoredRules = result.IgnoredViolations,
            disabledRules = result.DisabledRules,
            invalidCompatibilityRules = result.InvalidCompatibilityRules,
            fixesApplied = result.FixesApplied,
            fixesSkipped = result.FixesSkipped,
            fixErrors = result.FixErrors ?? Array.Empty<string>(),
            ruleLoadDiagnostics = result.RuleLoadDiagnostics ?? Array.Empty<string>(),
            saved = result.Saved,
            staged = result.Staged,
            results = result.Violations.Select(v => new
            {
                ruleId = v.RuleId,
                ruleName = v.RuleName,
                category = v.Category,
                severity = (int)v.Severity,
                severityLabel = v.Severity.ToString(),
                objectName = v.ObjectName,
                objectType = v.ObjectType,
                canFix = v.CanFix
            }),
            diagnostics = result.Results
                .Where(r => r.Kind != BpaResultKind.Violation)
                .Select(r => new
                {
                    kind = r.Kind.ToString(),
                    ruleId = r.RuleId,
                    ruleName = r.RuleName,
                    scope = r.ErrorScope,
                    message = r.ErrorMessage
                }),
            errors = Array.Empty<string>()
        };

    private static object ProjectRulesListJson(BpaRulesListResult result)
        => new
        {
            rules = result.Rules.Select(ProjectRuleInfo),
            summary = result.Summary
        };

    private static Dictionary<string, object?> ProjectRuleInfo(BpaRuleInfo rule)
    {
        var json = new Dictionary<string, object?>
        {
            ["source"] = rule.Source,
            ["status"] = rule.Status,
            ["id"] = rule.Id,
            ["name"] = rule.Name,
            ["category"] = rule.Category,
            ["severity"] = (int)rule.Severity,
            ["severityLabel"] = rule.Severity.ToString(),
            ["scope"] = rule.Scope
        };

        AddIfNotEmpty(json, "description", rule.Description);
        AddIfNotEmpty(json, "expression", rule.Expression);
        AddIfNotEmpty(json, "fixExpression", rule.FixExpression);

        return json;
    }

    private static void AddIfNotEmpty(Dictionary<string, object?> json, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            json[name] = value;
    }

    private sealed record RunViewOptions(
        bool NoMultiline,
        bool Full,
        bool Details,
        bool Errors,
        bool Warnings,
        bool Info);

    private const int DetailTextWidth = 84;

    private static void RenderRun(BpaRunResult result, RunViewOptions view)
    {
        AnsiConsole.MarkupLine(Styling.Title($"BPA analysis · {result.ModelName}"));

        if (result.Violations.Count == 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(Styling.Success("No BPA violations found."));
            RenderDiagnostics(result, view);
            return;
        }

        var errorCount = result.Violations.Count(v => v.Severity == BpaSeverity.Error);
        var warningCount = result.Violations.Count(v => v.Severity == BpaSeverity.Warning);
        var infoCount = result.Violations.Count(v => v.Severity == BpaSeverity.Info);

        var groups = BpaRunView.OrderRuleGroups(result.Violations);

        var summary = string.Join(" · ",
            $"{result.Violations.Count} findings",
            Styling.Error($"{errorCount} errors"),
            Styling.Warning($"{warningCount} warnings"),
            $"{infoCount} info",
            $"{groups.Count} rules");

        var filterActive = view.Errors || view.Warnings || view.Info;
        if (filterActive)
        {
            var shown = new List<string>(3);
            if (view.Errors) shown.Add("errors");
            if (view.Warnings) shown.Add("warnings");
            if (view.Info) shown.Add("info");
            summary += "  " + Styling.Muted($"(showing {string.Join(" + ", shown)})");
        }

        AnsiConsole.Write(new Rule().RuleStyle(new Style(Palette.Slate)));

        var visible = groups
            .Where(g => BpaRunView.MatchesFilter(g.Severity, view.Errors, view.Warnings, view.Info))
            .ToList();

        if (visible.Count == 0)
        {
            AnsiConsole.MarkupLine(Styling.Muted("  Nothing to show for the selected severities."));
        }
        else if (view.Details)
        {
            foreach (var group in visible)
                RenderRuleGroup(group, view);
        }
        else
        {
            RenderCompact(visible);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule().RuleStyle(new Style(Palette.Slate)));
        AnsiConsole.MarkupLine(summary);

        var fixable = result.Violations.Count(v => v.CanFix);
        if (fixable > 0)
            AnsiConsole.MarkupLine(
                Styling.Value($"{fixable} of {result.Violations.Count} can be auto-fixed")
                + Styling.Muted(" — run  bpa run --fix"));

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  {Styling.KeyValue("Rules evaluated:", result.RulesEvaluated.ToString())}");

        if (result.DurationMs > 0)
            AnsiConsole.MarkupLine($"  {Styling.KeyValue("Duration:", $"{result.DurationMs}ms")}");

        RenderDiagnostics(result, view);

            if (result.FixesApplied > 0)
            {
                AnsiConsole.MarkupLine($"  {Styling.KeyValue("Fixes applied:", result.FixesApplied.ToString())}");
                if (result.FixesSkipped > 0)
                    AnsiConsole.MarkupLine($"  {Styling.KeyValue("Fixes skipped:", result.FixesSkipped.ToString())}");
                if (result.Saved is true or string)
                    AnsiConsole.MarkupLine($"  {Styling.Success("Model saved.")}");
                else if (result.Staged == true)
                    AnsiConsole.MarkupLine($"  {Styling.Success("Mutation staged.")}");

                if (result.Synced)
                    AnsiConsole.MarkupLine($"  {Styling.Success($"Synced: {Styling.MarkupEscape(result.SyncTarget!)}")}");
                else if (result.SyncWarning is not null)
                    AnsiConsole.MarkupLine($"  {Styling.Warning(Styling.MarkupEscape(result.SyncWarning))}");
            }
        else if (result.FixesSkipped > 0)
        {
            AnsiConsole.MarkupLine($"  {Styling.KeyValue("Fixes skipped:", result.FixesSkipped.ToString())}");
        }

        if (result.FixErrors is { Count: > 0 })
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"  {Styling.Error("Fix errors:")}");
            foreach (var err in result.FixErrors)
                AnsiConsole.MarkupLine("    {0}", Styling.MarkupEscape(err));
        }

        if (visible.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(Styling.Guidance(view.Details
                ? "Run with --full to list every affected object, or --rule <ID> to focus a single rule."
                : "Run  bpa run --details  for guidance, or  --rule <ID>  to focus a single rule."));
        }
    }

    /// <summary>
    /// Footer for the non-violation result kinds. Always shows a one-line count summary when any
    /// are present; lists the individual diagnostics only under --details so default output stays
    /// violation-focused.
    /// </summary>
    private static void RenderDiagnostics(BpaRunResult result, RunViewOptions view)
    {
        if (result.RuleLoadDiagnostics is { Count: > 0 } loadDiagnostics)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"  {Styling.Warning("Rule loading:")}");
            foreach (var diag in loadDiagnostics)
                AnsiConsole.MarkupLine("    {0}", Styling.MarkupEscape(diag));
        }

        var parts = new List<string>(4);
        if (result.RuleErrors > 0) parts.Add($"{result.RuleErrors} rule errors");
        if (result.DisabledRules > 0) parts.Add($"{result.DisabledRules} disabled");
        if (result.InvalidCompatibilityRules > 0) parts.Add($"{result.InvalidCompatibilityRules} skipped (compat level)");
        if (result.IgnoredViolations > 0) parts.Add($"{result.IgnoredViolations} ignored");

        if (parts.Count == 0)
            return;

        AnsiConsole.MarkupLine($"  {Styling.KeyValue("Diagnostics:", string.Join(" · ", parts))}");

        if (!view.Details)
        {
            AnsiConsole.MarkupLine(Styling.Muted("  Run  bpa run --details  to list diagnostics."));
            return;
        }

        var diagnostics = result.Results
            .Where(r => r.Kind != BpaResultKind.Violation)
            .ToList();

        if (diagnostics.Count == 0)
            return;

        AnsiConsole.WriteLine();
        foreach (var diag in diagnostics)
        {
            var label = diag.Kind switch
            {
                BpaResultKind.CompilationError => "compile",
                BpaResultKind.EvaluationError => "evaluate",
                BpaResultKind.InvalidCompatibilityLevel => "compat",
                BpaResultKind.DisabledRule => "disabled",
                _ => diag.Kind.ToString()
            };

            var scope = string.IsNullOrWhiteSpace(diag.ErrorScope) ? "" : $" ({diag.ErrorScope})";
            var detail = string.IsNullOrWhiteSpace(diag.ErrorMessage) ? "" : $" — {diag.ErrorMessage}";
            AnsiConsole.MarkupLine(
                "    {0} {1}{2}{3}",
                Styling.Muted($"[{label}]"),
                Styling.MarkupEscape(diag.RuleId),
                Styling.MarkupEscape(scope),
                Styling.Muted(Styling.MarkupEscape(detail)));
        }
    }

    private static void RenderCompact(IReadOnlyList<BpaRunView.RuleGroup> groups)
    {
        var table = new Table().Border(TableBorder.None);
        table.AddColumn(new TableColumn(Styling.Muted("SEVERITY")));
        table.AddColumn(new TableColumn(Styling.Muted("CATEGORY")));
        table.AddColumn(new TableColumn(Styling.Muted("RULE / ID")));
        table.AddColumn(new TableColumn(Styling.Muted("COUNT")).RightAligned());

        foreach (var group in groups)
        {
            var name = BpaRunView.StripCategoryPrefix(group.RuleName, group.Category);
            // Rule name on line 1, the (copy-able) rule id dimmed on line 2 of the same cell,
            // so the severity/category/count columns stay aligned regardless of id length.
            var rule = $"{Styling.Bold(name)}\n{Styling.Muted(group.RuleId)}";
            table.AddRow(
                Styling.SeverityHeading(BpaRunView.SeverityWord(group.Severity)),
                Styling.MarkupEscape(group.Category),
                rule,
                Styling.Muted($"×{group.Objects.Count}"));
        }

        AnsiConsole.Write(table);
    }

    private static void RenderRuleGroup(BpaRunView.RuleGroup group, RunViewOptions view)
    {
        AnsiConsole.WriteLine();

        // Wrap to the effective render width so Spectre never re-wraps (which would
        // split words). Account for the 2-space indent; cap at DetailTextWidth.
        var width = Math.Max(24, Math.Min(DetailTextWidth, AnsiConsole.Profile.Width - 2));

        var word = BpaRunView.SeverityWord(group.Severity);

        var header = new Grid().Expand();
        header.AddColumn();
        header.AddColumn(new GridColumn().RightAligned());
        header.AddRow(
            $"{Styling.SeverityHeading(word)}  {Styling.MarkupEscape(group.Category)}",
            Styling.Muted($"×{group.Objects.Count}"));
        AnsiConsole.Write(header);

        var name = BpaRunView.StripCategoryPrefix(group.RuleName, group.Category);
        AnsiConsole.MarkupLine($"  {Styling.Bold(name)}  {Styling.Muted($"[{group.RuleId}]")}");

        var guidance = BpaRunView.Guidance(group.Description, view.NoMultiline);
        if (guidance.Length > 0)
            foreach (var line in BpaRunView.WrapText(guidance, width))
                AnsiConsole.MarkupLine($"  {Styling.Guidance(line)}");

        var objects = BpaRunView.FormatObjectList(group.Objects, view.Full);
        if (objects.Length > 0)
            foreach (var line in BpaRunView.WrapText($"Affects  {objects}", width))
                AnsiConsole.MarkupLine($"  {Styling.Muted(line)}");
    }

    private static string CollapseDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return "";

        var firstLine = description.Split('\n', 2)[0].TrimEnd('\r');

        var refIdx = firstLine.IndexOf("Reference:", StringComparison.OrdinalIgnoreCase);
        if (refIdx > 0)
            firstLine = firstLine[..refIdx].TrimEnd();

        return firstLine;
    }

    private static string SeverityMarkup(BpaSeverity severity)
        => Styling.SeverityMarkup(severity switch
        {
            BpaSeverity.Error => "Error",
            BpaSeverity.Warning => "Warning",
            _ => "Info"
        });

    private static void RenderRulesList(BpaRulesListResult result)
    {
        if (result.Rules.Count == 0)
        {
            AnsiConsole.MarkupLine(Styling.Warning("No BPA rules available."));
            return;
        }

        var table = Styling.NewTable("ID", "Name", "Category", "Severity");

        foreach (var r in result.Rules)
        {
            table.AddRow(
                Styling.MarkupEscape(Truncate(r.Id, 45)),
                Styling.MarkupEscape(Truncate(r.Name, 55)),
                Styling.MarkupEscape(Truncate(r.Category, 20)),
                SeverityMarkup(r.Severity));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  {Styling.KeyValue("Total rules:", result.Rules.Count.ToString())}");
    }

    private static void EmitCi(string? ci, IReadOnlyList<BpaViolation> violations)
    {
        if (string.IsNullOrWhiteSpace(ci) || violations.Count == 0)
            return;

        foreach (var v in violations)
        {
            var msg = $"{v.RuleName}: {v.ObjectType} '{v.ObjectName}'";
            if (!string.IsNullOrWhiteSpace(v.Description))
            {
                var shortDesc = CollapseDescription(v.Description);
                msg += $" - {shortDesc}";
            }

            if (ci.Equals("github", StringComparison.OrdinalIgnoreCase))
            {
                var level = v.Severity == BpaSeverity.Error ? "error" : "warning";
                Console.Error.WriteLine($"::{level}::{msg} [{v.RuleId}]");
            }
            else if (ci.Equals("vsts", StringComparison.OrdinalIgnoreCase))
            {
                var type = v.Severity == BpaSeverity.Error ? "error" : "warning";
                Console.Error.WriteLine($"##vso[task.logissue type={type};]{msg} [{v.RuleId}]");
            }
        }

        var errors = violations.Count(v => v.Severity == BpaSeverity.Error);
        if (errors > 0 && ci.Equals("vsts", StringComparison.OrdinalIgnoreCase))
            Console.Error.WriteLine("##vso[task.complete result=Failed;]Done.");
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..Math.Max(0, maxLength - 3)] + "...";
}
