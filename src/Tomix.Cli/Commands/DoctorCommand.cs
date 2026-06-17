using System.CommandLine;
using Tomix.App.Doctor;
using Tomix.Cli.Output;
using Tomix.Core.Doctor;
using Spectre.Console;

namespace Tomix.Cli.Commands;

internal sealed class DoctorCommand : ICommandModule
{
    private readonly string _version;

    public DoctorCommand(string version) => _version = version;

    public Command Build()
    {
        var format = OutputFormats.CreateOption();

        var command = new Command("doctor", "Check whether the local tomix environment is ready.")
        {
            format
        };

        command.SetAction(parseResult =>
        {
            var formatValue = parseResult.GetValue(format) ?? OutputFormats.Text;

            if (!CommandOutput.TryValidateFormat(formatValue))
                return 2;

            var result = new DoctorHandler().Handle(_version);
            return CommandOutput.Render(result, formatValue, Render);
        });

        return command;
    }

    private static void Render(DoctorResult result)
    {
        AnsiConsole.MarkupLine(Styling.Title("tx doctor"));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(Styling.KeyValue("Version:          ", result.Version));
        AnsiConsole.MarkupLine(Styling.KeyValue("Operating system: ", result.OperatingSystem));
        AnsiConsole.MarkupLine(Styling.KeyValue(".NET version:     ", result.DotNetVersion));
        AnsiConsole.MarkupLine(Styling.KeyValue("Config directory: ", result.ConfigDirectory));
        AnsiConsole.WriteLine();

        var caps = AnsiConsole.Profile.Capabilities;
        AnsiConsole.MarkupLine(Styling.KeyValue("Interactive:      ", caps.Interactive.ToString()));
        AnsiConsole.MarkupLine(Styling.KeyValue("Ansi:             ", caps.Ansi.ToString()));
        AnsiConsole.MarkupLine(Styling.KeyValue("Color system:     ", caps.ColorSystem.ToString()));
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
