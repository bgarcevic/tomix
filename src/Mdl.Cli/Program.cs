using System.CommandLine;
using System.Reflection;
using Mdl.Cli.Commands;
using Mdl.Core.Models;
using Mdl.Provider.Tmdl;

namespace Mdl.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        var version = ResolveVersion();
        var root = new RootCommand("MDL - the open semantic model CLI");

        IReadOnlyList<IModelProvider> providers = [new TmdlModelProvider()];

        var modules = new ICommandModule[]
        {
            new DoctorCommand(version),
            new ConfigCommand(),
            new CompletionCommand(() => root.Subcommands.Select(command => command.Name).ToList()),
            new InfoCommand(providers),
            new LsCommand(providers)
        };

        foreach (var module in modules)
            root.Subcommands.Add(module.Build());

        return root.Parse(args).Invoke();
    }

    private static string ResolveVersion()
    {
        var informational = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
            return "0.0.0";

        // Strip any "+<commit>" SourceLink suffix so doctor reports a clean version.
        var plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }
}
