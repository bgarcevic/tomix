using System.CommandLine;
using Mdl.App.Doctor;
using Mdl.Cli.Output;
using Mdl.Core.Doctor;

namespace Mdl.Cli.Commands;

internal sealed class DoctorCommand : ICommandModule
{
    private readonly string _version;

    public DoctorCommand(string version) => _version = version;

    public Command Build()
    {
        var format = OutputFormats.CreateOption();

        var command = new Command("doctor", "Check whether the local MDL environment is ready.")
        {
            format
        };

        command.SetAction(parseResult =>
        {
            var formatValue = parseResult.GetValue(format) ?? OutputFormats.Human;

            if (!CommandOutput.TryValidateFormat(formatValue))
                return 2;

            var result = new DoctorHandler().Handle(_version);
            return CommandOutput.Render(result, formatValue, Render);
        });

        return command;
    }

    private static void Render(DoctorResult result)
    {
        Console.WriteLine("MDL doctor");
        Console.WriteLine();
        Console.WriteLine($"Version:          {result.Version}");
        Console.WriteLine($"Operating system: {result.OperatingSystem}");
        Console.WriteLine($".NET version:     {result.DotNetVersion}");
        Console.WriteLine($"Config directory: {result.ConfigDirectory}");
        Console.WriteLine();

        foreach (var check in result.Checks)
        {
            var status = check.Status switch
            {
                DoctorCheckStatus.Pass => "OK",
                DoctorCheckStatus.Warning => "WARN",
                DoctorCheckStatus.Fail => "FAIL",
                _ => "UNKNOWN"
            };

            Console.WriteLine($"{status,-7} {check.Name}: {check.Message}");
        }
    }
}
