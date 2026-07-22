using System.CommandLine;
using Spectre.Console;
using Tomix.App.Config;
using Tomix.App.Doctor;
using Tomix.App.State;
using Tomix.App.Update;
using Tomix.Cli.Output;
using Tomix.Core.Doctor;

namespace Tomix.Cli.Commands;

internal sealed class DoctorCommand : ICommandModule
{
    private readonly string _version;
    private readonly DoctorHandler _handler;

    public DoctorCommand(
        string version,
        string configDirectory,
        TomixConfigStore configStore,
        CliStateStore state,
        UpdateCheckStore updateStore,
        string authMetadataFile,
        IReadOnlyList<string> providerNames,
        string? configLoadError = null)
    {
        _version = version;
        _handler = new DoctorHandler(
            configDirectory, configStore, state, updateStore, authMetadataFile, providerNames, configLoadError);
    }

    public Command Build()
    {
        var format = OutputFormats.CreateOption(GlobalOptions.DefaultOutputFormat);
        var command = new Command("doctor", "Check whether the local tomix environment is ready.")
        {
            format
        };

        command.SetAction(parseResult =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult, format);
            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "doctor", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var caps = AnsiConsole.Profile.Capabilities;
            var result = _handler.Handle(
                _version,
                new DoctorTerminalCapabilities(caps.Interactive, caps.Ansi, caps.ColorSystem.ToString()));
            return CommandOutput.Render(result, formatValue, Render);
        });

        return command;
    }

    private static void Render(DoctorResult result)
    {
        AnsiConsole.MarkupLine(Styling.Title("tx doctor"));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(Styling.KeyValue("Version:          ", result.Version));
        if (result.LatestVersion is not null)
            AnsiConsole.MarkupLine(Styling.KeyValue("Latest version:   ", result.LatestVersion));
        AnsiConsole.MarkupLine(Styling.KeyValue("Operating system: ", result.OperatingSystem));
        AnsiConsole.MarkupLine(Styling.KeyValue(".NET version:     ", result.DotNetVersion));
        AnsiConsole.MarkupLine(Styling.KeyValue("Config directory: ", result.ConfigDirectory));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(Styling.KeyValue("Interactive:      ", result.Terminal.Interactive.ToString()));
        AnsiConsole.MarkupLine(Styling.KeyValue("Ansi:             ", result.Terminal.Ansi.ToString()));
        AnsiConsole.MarkupLine(Styling.KeyValue("Color system:     ", result.Terminal.ColorSystem));
        AnsiConsole.WriteLine();

        foreach (var check in result.Checks)
        {
            var statusMarkup = check.Status switch
            {
                DoctorCheckStatus.Pass => Styling.Success("OK"),
                DoctorCheckStatus.Warning => Styling.Warning("WARN"),
                DoctorCheckStatus.Fail => Styling.Error("FAIL"),
                _ => Styling.Muted("UNKNOWN")
            };
            AnsiConsole.MarkupLine($"{statusMarkup} {Styling.MarkupEscape(check.Name)}: {Styling.MarkupEscape(check.Message)}");
        }
    }
}
