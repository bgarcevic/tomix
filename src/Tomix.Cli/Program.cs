using System.CommandLine;
using System.CommandLine.Help;
using System.Reflection;
using System.Text;
using Spectre.Console;
using Tomix.App;
using Tomix.App.Auth;
using Tomix.App.Config;
using Tomix.App.Connect;
using Tomix.App.Format;
using Tomix.App.Update;
using Tomix.Auth;
using Tomix.Cli.Commands;
using Tomix.Cli.Output;
using Tomix.Core.Configuration;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;
using Tomix.Core.Vertipaq;
using Tomix.Provider.Tmdl;
using Tomix.Provider.Tom;
using Tomix.Provider.Vpax;

namespace Tomix.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        var services = AppServices.Create();
        IDictionary<string, string> config;
        try
        {
            config = services.ConfigStore.Load();
        }
        catch (InvalidOperationException ex)
        {
            // Config loads before argument parsing, so the top-level exception handler
            // cannot catch this. A corrupt config must still fail with the actionable
            // message, not an unhandled stack trace.
            ErrorOutput.Write(
                [new TomixDiagnostic(
                    "TOMIX_CONFIG_CORRUPT",
                    DiagnosticSeverity.Error,
                    ex.Message,
                    "Fix or delete the file, then re-create settings with 'tx config set'.")],
                format: null);
            return 2;
        }
        // Per https://no-color.org: only a non-empty value disables color.
        var noColorEnv = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
        var noColorCfg = config.TryGetValue(ConfigKeys.NoColor, out var noColor) && bool.TryParse(noColor, out var noColorEnabled) && noColorEnabled;
        if (noColorEnv || noColorCfg)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        var tokenProvider = new MsalAuthenticator(
            App.Auth.AuthSettingsFactory.Resolve(config),
            messageWriter: Console.Error.WriteLine);
        IReadOnlyList<IModelProvider> providers =
            [new TmdlModelProvider(tokenProvider), new TomFileModelProvider(tokenProvider), new TomServerModelProvider(tokenProvider)];
        // Explicit timeout so hung formatter/REST endpoints fail predictably instead of
        // holding the command for HttpClient's 100s default.
        var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var formatter = new CompositeExpressionFormatterClient(
            [
                new DaxFormatterApiClient(),
                new PowerQueryFormatterApiClient(httpClient)
            ]);
        var workspaceCatalog = new PowerBiWorkspaceCatalog(httpClient, tokenProvider);
        var releaseSource = new GitHubReleaseSource(httpClient);

        var version = ResolveVersion();
        var root = BuildRootCommand(
            providers, formatter, version, services, workspaceCatalog, tokenProvider.CachedUsername,
            new VpaxVertipaqAnalyzer(tokenProvider, version), releaseSource);

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

        var exitCode = Invoke(parseResult);
        UpdateNotice.Run(parseResult, version, config, services.UpdateCheck, releaseSource);
        return exitCode;
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
        catch (AmbiguousModelProviderException ex)
        {
            ErrorOutput.Write(
                [new TomixDiagnostic(
                    "TOMIX_PROVIDER_AMBIGUOUS",
                    DiagnosticSeverity.Error,
                    ex.Message,
                    "Report this at https://github.com/bgarcevic/tomix-cli/issues.")],
                parseResult.GetValue(GlobalOptions.ErrorFormat));
            return 1;
        }
        catch (Exception ex)
        {
            // Unexpected failures still follow the error contract: stable code via
            // ErrorOutput, stack trace only under --debug (docs/cli-ux-guidelines.md).
            // The trace rides inside the envelope in JSON mode so stderr stays parseable.
            ErrorOutput.Write(
                [new TomixDiagnostic(
                    "TOMIX_UNEXPECTED",
                    DiagnosticSeverity.Error,
                    $"Unexpected error: {ex.Message}",
                    "Re-run with --debug for the full stack trace; if this persists, report it at https://github.com/bgarcevic/tomix-cli/issues.")],
                parseResult.GetValue(GlobalOptions.ErrorFormat),
                detail: parseResult.GetValue(GlobalOptions.Debug) ? ex.ToString() : null);

            return 1;
        }
    }

    internal static RootCommand BuildRootCommand(
        IReadOnlyList<IModelProvider> providers,
        IExpressionFormatterClient formatter,
        string version,
        AppServices services,
        IWorkspaceCatalog? workspaceCatalog = null,
        Func<string?>? cachedUsername = null,
        IVertipaqAnalyzer? analyzer = null,
        IReleaseSource? releaseSource = null)
    {
        analyzer ??= new VpaxVertipaqAnalyzer(tokenProvider: null, version);
        var root = new RootCommand("tx - CLI for semantic models");
        foreach (var option in GlobalOptions.All())
            root.Options.Add(option);

        var modules = new ICommandModule[]
        {
            new AddCommand(providers, services),
            new AuthCommand(services),
            new BpaCommand(providers, services),
            new CompletionCommand(() => root.Subcommands.Select(command => command.Name).ToList()),
            new ConfigCommand(services),
            new ConnectCommand(
                providers,
                workspaceCatalog ?? EmptyWorkspaceCatalog.Instance,
                cachedUsername ?? (() => null),
                services),
            new DeployCommand(providers, services),
            new DepsCommand(providers, services),
            new DiffCommand(providers),
            new DoctorCommand(version, services.ConfigDirectory, releaseSource),
            new FindCommand(providers, services),
            new FormatCommand(providers, formatter, services),
            new GetCommand(providers, services),
            new IncrementalRefreshCommand(providers, services),
            new InitCommand(),
            new LoadCommand(providers, services),
            new LsCommand(providers, services),
            new MvCommand(providers, services),
            new ProfileCommand(services),
            new QueryCommand(providers, services),
            new RefreshCommand(providers, services),
            new ReplaceCommand(providers, services),
            new RmCommand(providers, services),
            new SaveCommand(providers, services),
            new ScriptCommand(providers, services),
            new SessionCommand(services),
            new SetCommand(providers, services),
            new StageCommand(providers, services),
            new ValidateCommand(providers, services),
            new VertipaqCommand(providers, analyzer, services)
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
