using System.CommandLine;
using System.CommandLine.Help;
using System.Reflection;
using System.Text;
using Tomix.App.Auth;
using Tomix.App.Config;
using Tomix.App.Connect;
using Tomix.App.Format;
using Tomix.Auth;
using Tomix.Cli.Commands;
using Tomix.Cli.Output;
using Tomix.Core.Configuration;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;
using Tomix.Core.Vertipaq;
using Tomix.Provider.Tom;
using Tomix.Provider.Tmdl;
using Tomix.Provider.Vpax;
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
        var workspaceCatalog = new PowerBiWorkspaceCatalog(new HttpClient(), tokenProvider);

        var version = ResolveVersion();
        var root = BuildRootCommand(
            providers, formatter, version, workspaceCatalog, tokenProvider.CachedUsername,
            new VpaxVertipaqAnalyzer(tokenProvider, version));

        if (args.Length == 0)
        {
            root.Parse(["--help"]).Invoke();
            return 0;
        }

        var parseResult = root.Parse(args);
        if (parseResult.Errors.Count == 0 && UnknownOptionGuard.TryReject(parseResult, args))
            return 2;

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

            // Invoke prints the parse errors, but returns System.CommandLine's default of 1;
            // usage errors exit 2 per the documented contract (docs/error-codes.md).
            parseResult.Invoke();
            return 2;
        }

        return Invoke(parseResult);
    }

    /// <summary>
    /// Invokes the parsed command with the library's default exception handler disabled so
    /// provider load failures surface as diagnostics instead of raw stack traces.
    /// </summary>
    internal static int Invoke(ParseResult parseResult)
    {
        try
        {
            return parseResult.Invoke(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (ModelLoadException ex)
        {
            ErrorOutput.Write(
                [new TomixDiagnostic(
                    "TOMIX_MODEL_LOAD_FAILED",
                    DiagnosticSeverity.Error,
                    ex.Message,
                    "Fix the model source and retry; the message lists what could not be loaded.")],
                parseResult.GetValue(GlobalOptions.ErrorFormat));
            return 2;
        }
        catch (Exception ex)
        {
            // Preserve the previous behavior for genuinely unexpected exceptions.
            Console.Error.WriteLine($"Unhandled exception: {ex}");
            return 1;
        }
    }

    internal static RootCommand BuildRootCommand(
        IReadOnlyList<IModelProvider> providers,
        IExpressionFormatterClient formatter,
        string version,
        IWorkspaceCatalog? workspaceCatalog = null,
        Func<string?>? cachedUsername = null,
        IVertipaqAnalyzer? analyzer = null)
    {
        analyzer ??= new VpaxVertipaqAnalyzer(tokenProvider: null, version);
        var root = new RootCommand("tx - CLI for semantic models");
        foreach (var option in GlobalOptions.All())
            root.Options.Add(option);

        var stubs = CompatibilityStubCommand.All().ToDictionary(command => command.Name);

        var modules = new ICommandModule[]
        {
            new AddCommand(providers),
            new AuthCommand(),
            new BpaCommand(providers),
            new CompletionCommand(() => root.Subcommands.Select(command => command.Name).ToList()),
            new ConfigCommand(),
            new ConnectCommand(
                providers,
                workspaceCatalog ?? EmptyWorkspaceCatalog.Instance,
                cachedUsername ?? (() => null)),
            new DeployCommand(providers),
            new DepsCommand(providers),
            new DiffCommand(providers),
            new DoctorCommand(version),
            new FindCommand(providers),
            new FormatCommand(providers, formatter),
            new GetCommand(providers),
            new IncrementalRefreshCommand(providers),
            new InitCommand(),
            new InteractiveCommand(),
            new LoadCommand(providers),
            new LsCommand(providers),
            new MvCommand(providers),
            new ProfileCommand(),
            stubs["query"],
            new RefreshCommand(providers),
            new ReplaceCommand(providers),
            new RmCommand(providers),
            new SaveCommand(providers),
            new ScriptCommand(providers),
            new SessionCommand(),
            new SetCommand(providers),
            new StageCommand(providers),
            new ValidateCommand(providers),
            new VertipaqCommand(providers, analyzer)
        };

        foreach (var module in modules)
            root.Subcommands.Add(module.Build());

        ApplySpectreHelp(root);
        return root;
    }

    private static void ApplySpectreHelp(Command command)
    {
        var helpOption = command.Options.OfType<HelpOption>().FirstOrDefault();
        if (helpOption is not null)
            helpOption.Action = new SpectreHelpAction();

        foreach (var sub in command.Subcommands)
            ApplySpectreHelp(sub);
    }

    /// <summary>No-op workspace catalog for contexts that never prompt (e.g. help-only test roots).</summary>
    private sealed class EmptyWorkspaceCatalog : IWorkspaceCatalog
    {
        public static readonly EmptyWorkspaceCatalog Instance = new();

        public Task<IReadOnlyList<WorkspaceInfo>> ListWorkspacesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<WorkspaceInfo>>([]);
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
