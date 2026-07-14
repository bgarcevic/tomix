using System.CommandLine;
using Tomix.App.Profile;
using Tomix.App.State;
using Tomix.Cli.Output;
using Spectre.Console;

namespace Tomix.Cli.Commands;

internal sealed class ProfileCommand : ICommandModule
{
    public Command Build()
    {
        var command = new Command("profile", "Manage named connection profiles for quick environment switching");
        command.Subcommands.Add(BuildList());
        command.Subcommands.Add(BuildRemove());
        command.Subcommands.Add(BuildSet());
        command.Subcommands.Add(BuildShow());
        return command;
    }

    private static Command BuildList()
    {
        var command = new Command("list", "List all saved connection profiles");
        command.SetAction(parseResult =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(format, "profile list", OutputFormats.Text, OutputFormats.Json))
                return 2;

            return CommandOutput.Render(new ProfileHandler().List(), format, RenderList);
        });
        return command;
    }

    private static Command BuildShow()
    {
        var nameArgument = new Argument<string>("name") { Description = "Profile name to show" };
        var command = new Command("show", "Show details of a saved connection profile")
        {
            nameArgument
        };

        command.SetAction(parseResult =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(format, "profile show", OutputFormats.Text, OutputFormats.Json))
                return 2;

            return CommandOutput.Render(
                new ProfileHandler().Show(parseResult.GetValue(nameArgument) ?? ""),
                format,
                result => RenderProfile(result.Profile));
        });
        return command;
    }

    private static Command BuildRemove()
    {
        var nameArgument = new Argument<string>("name") { Description = "Profile name to remove" };
        var command = new Command("remove", "Delete a saved connection profile")
        {
            nameArgument
        };

        command.SetAction(parseResult =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(format, "profile remove", OutputFormats.Text, OutputFormats.Json))
                return 2;

            return CommandOutput.Render(
                new ProfileHandler().Remove(parseResult.GetValue(nameArgument) ?? ""),
                format,
                result => AnsiConsole.MarkupLine(result.Removed ? Styling.Success($"Removed: {result.Name}") : Styling.Warning($"Not found: {result.Name}")));
        });
        return command;
    }

    private static Command BuildSet()
    {
        var nameArgument = new Argument<string>("name") { Description = "Profile name" };
        var descriptionOption = new Option<string?>("--description") { Description = "Human-readable description of this profile" };
        descriptionOption.Aliases.Add("--desc");
        var fromActiveOption = new Option<bool>("--from-active") { Description = "Save the current active connection as this profile" };
        var autoFormatOption = new Option<string?>("--auto-format") { Description = "Override autoFormat (true/false/null to clear)" };
        var validateOption = new Option<string?>("--validate-on-mutation") { Description = "Override validateOnMutation" };
        var bpaMutationOption = new Option<string?>("--bpa-on-mutation") { Description = "Override bpa.onMutation" };
        var bpaDeployOption = new Option<string?>("--bpa-on-deploy") { Description = "Override bpa.onDeploy" };
        var vertipaqOption = new Option<string?>("--vertipaq-on-refresh") { Description = "Override vertipaqOnRefresh" };
        var spinnerOption = new Option<string?>("--spinner") { Description = "Override spinner" };

        var command = new Command("set", "Create or update a named connection profile")
        {
            nameArgument,
            descriptionOption,
            fromActiveOption,
            autoFormatOption,
            validateOption,
            bpaMutationOption,
            bpaDeployOption,
            vertipaqOption,
            spinnerOption
        };

        command.SetAction(parseResult =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(format, "profile set", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var result = new ProfileHandler().Set(new ProfileSetRequest(
                parseResult.GetValue(nameArgument) ?? "",
                parseResult.GetValue(GlobalOptions.Server),
                parseResult.GetValue(GlobalOptions.Database),
                GlobalOptions.ModelValue(parseResult),
                GlobalOptions.AuthValue(parseResult),
                parseResult.GetValue(descriptionOption),
                ParseNullableBool(parseResult.GetValue(autoFormatOption)),
                ParseNullableBool(parseResult.GetValue(validateOption)),
                ParseNullableBool(parseResult.GetValue(bpaMutationOption)),
                ParseNullableBool(parseResult.GetValue(bpaDeployOption)),
                ParseNullableBool(parseResult.GetValue(vertipaqOption)),
                ParseNullableBool(parseResult.GetValue(spinnerOption))));

            return CommandOutput.Render(
                result,
                format,
                data => AnsiConsole.MarkupLine(Styling.Success($"Saved profile: {data.Profile.Name}")));
        });

        return command;
    }

    private static void RenderList(ProfileListResult result)
    {
        if (result.Profiles.Count == 0)
            return;

        foreach (var profile in result.Profiles.Values)
            AnsiConsole.WriteLine($"{profile.Name}\t{profile.Model ?? profile.Server ?? ""}\t{profile.Database ?? ""}");
    }

    private static void RenderProfile(CliProfile profile)
    {
        AnsiConsole.MarkupLine(Styling.KeyValue("name:", $"        {profile.Name}"));
        AnsiConsole.MarkupLine(Styling.KeyValue("server:", $"      {profile.Server ?? ""}"));
        AnsiConsole.MarkupLine(Styling.KeyValue("database:", $"    {profile.Database ?? ""}"));
        AnsiConsole.MarkupLine(Styling.KeyValue("model:", $"       {profile.Model ?? ""}"));
        AnsiConsole.MarkupLine(Styling.KeyValue("auth:", $"        {profile.Auth ?? ""}"));
        AnsiConsole.MarkupLine(Styling.KeyValue("description:", $" {profile.Description ?? ""}"));
    }

    private static bool? ParseNullableBool(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("null", StringComparison.OrdinalIgnoreCase))
            return null;

        return bool.TryParse(value, out var parsed) ? parsed : null;
    }
}
