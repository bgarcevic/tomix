using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mdl.App.Doctor;
using Mdl.Core.Doctor;

namespace Mdl.Cli;

internal static class Program
{
    private const string Version = "0.1.0-dev";

    private static int Main(string[] args)
    {
        var root = new RootCommand("MDL - the open semantic model CLI");

        var formatOption = new Option<string>("--format")
        {
            Description = "Output format: human or json.",
            DefaultValueFactory = _ => "human"
        };

        formatOption.Aliases.Add("-f");

        var doctorCommand = new Command(
            name: "doctor",
            description: "Check whether the local MDL environment is ready.")
        {
            formatOption
        };

        doctorCommand.SetAction(parseResult =>
        {
            var format = parseResult.GetValue(formatOption) ?? "human";

            if (format is not "human" and not "json")
            {
                Console.Error.WriteLine("Invalid --format value. Expected: human or json.");
                return 2;
            }

            var handler = new DoctorHandler();
            var result = handler.Handle(Version);

            if (result.Data is null)
            {
                Console.Error.WriteLine("Doctor command failed.");
                return result.ExitCode;
            }

            if (format == "json")
            {
                RenderJson(result.Data);
            }
            else
            {
                RenderHuman(result.Data);
            }

            return result.ExitCode;
        });

        root.Subcommands.Add(doctorCommand);

        return root.Parse(args).Invoke();
    }

    private static void RenderHuman(DoctorResult result)
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

    private static void RenderJson(DoctorResult result)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        options.Converters.Add(new JsonStringEnumConverter());

        Console.WriteLine(JsonSerializer.Serialize(result, options));
    }
}