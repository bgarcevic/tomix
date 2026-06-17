using System.CommandLine;
using System.CommandLine.Help;
using System.Reflection;
using System.Text;
using Tomix.App.Auth;
using Tomix.App.Config;
using Tomix.App.Format;
using Tomix.Auth;
using Tomix.Cli.Commands;
using Tomix.Cli.Output;
using Tomix.Core.Configuration;
using Tomix.Core.Models;
using Tomix.Provider.Tom;
using Tomix.Provider.Tmdl;
using Spectre.Console;

namespace Tomix.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        var config = new TomixConfigStore().Load();
        var noColorEnv = Environment.GetEnvironmentVariable("NO_COLOR") is not null;
        var noColorCfg = config.TryGetValue(ConfigKeys.NoColor, out var noColor) && bool.TryParse(noColor, out var noColorEnabled) && noColorEnabled;
        if (noColorEnv || noColorCfg)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        var root = new RootCommand("tx - CLI for semantic models");
        foreach (var option in GlobalOptions.All())
            root.Options.Add(option);

        var tokenProvider = new MsalAuthenticator(
            App.Auth.AuthSettingsFactory.Resolve(),
            messageWriter: Console.Error.WriteLine);
        IReadOnlyList<IModelProvider> providers =
            [new TmdlModelProvider(tokenProvider), new TomFileModelProvider(tokenProvider), new TomServerModelProvider(tokenProvider)];
        var formatter = new CompositeExpressionFormatterClient(
            [
                new DaxFormatterApiClient(),
                new PowerQueryFormatterApiClient(new HttpClient())
            ]);
        var stubs = CompatibilityStubCommand.All().ToDictionary(command => command.Name);

        var modules = new ICommandModule[]
        {
            new AddCommand(providers),
            new AuthCommand(),
            new BpaCommand(providers),
            new CompletionCommand(() => root.Subcommands.Select(command => command.Name).ToList()),
            new ConfigCommand(),
            new ConnectCommand(providers),
            new DeployCommand(providers),
            new DepsCommand(providers),
            new DiffCommand(providers),
            new DoctorCommand(ResolveVersion()),
            new FindCommand(providers),
            new FormatCommand(providers, formatter),
            new GetCommand(providers),
            stubs["incremental-refresh"],
            new InitCommand(),
            new InteractiveCommand(),
            new LoadCommand(providers),
            new LsCommand(providers),
            new MacroCommand(),
            new MvCommand(providers),
            new ProfileCommand(),
            stubs["query"],
            stubs["refresh"],
            new ReplaceCommand(providers),
            new RmCommand(providers),
            new SaveCommand(providers),
            new ScriptCommand(providers),
            new SessionCommand(),
            new SetCommand(providers),
            new StageCommand(providers),
            stubs["test"],
            new ValidateCommand(providers),
            stubs["vertipaq"]
        };

        foreach (var module in modules)
            root.Subcommands.Add(module.Build());

        ApplySpectreHelp(root);

        if (args.Length == 0)
        {
            root.Parse(["--help"]).Invoke();
            return 0;
        }

        var parseResult = root.Parse(args);
        if (parseResult.Errors.Count > 0)
        {
            var commandNames = root.Subcommands.Select(c => c.Name).ToList();
            foreach (var error in parseResult.Errors)
            {
                if (error.Message.Contains("is not a valid command", StringComparison.OrdinalIgnoreCase) ||
                    error.Message.Contains("Unrecognized command", StringComparison.OrdinalIgnoreCase) ||
                    error.Message.Contains("Required command was not provided", StringComparison.OrdinalIgnoreCase))
                {
                    var firstArg = args.FirstOrDefault(a => !a.StartsWith('-'));
                    if (firstArg is not null)
                        DidYouMean.WriteSuggestion(firstArg, commandNames);
                }
            }
        }

        return parseResult.Invoke();
    }

    private static void ApplySpectreHelp(Command command)
    {
        var helpOption = command.Options.OfType<HelpOption>().FirstOrDefault();
        if (helpOption is not null)
            helpOption.Action = new SpectreHelpAction();

        foreach (var sub in command.Subcommands)
            ApplySpectreHelp(sub);
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
