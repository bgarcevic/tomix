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
using Tomix.App.State;
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
    private static int Main(string[] args) => Run(args);

    internal static int Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        var services = AppServices.Create();
        IDictionary<string, string> config;
        InvalidOperationException? configLoadError = null;
        try
        {
            config = services.ConfigStore.Load();
        }
        catch (InvalidOperationException ex)
        {
            // Parse with an empty fallback so help, doctor, and config recovery commands
            // remain available. All other commands are rejected after parsing below.
            config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            configLoadError = ex;
        }
        // Per https://no-color.org: only a non-empty value disables color.
        var noColorEnv = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
        var noColorCfg = config.TryGetValue(ConfigKeys.NoColor, out var noColor) && bool.TryParse(noColor, out var noColorEnabled) && noColorEnabled;
        if (noColorEnv || noColorCfg)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        config.TryGetValue(ConfigKeys.DefaultFormat, out var defaultOutputFormat);
        GlobalOptions.ConfigureDefaultOutputFormat(defaultOutputFormat);

        var tokenProvider = new MsalAuthenticator(
            AuthSettingsFactory.Resolve(config),
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
            new VpaxVertipaqAnalyzer(tokenProvider, version), releaseSource, httpClient,
            configLoadError?.Message);

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

        if (configLoadError is not null && !CanRunWithCorruptConfig(parseResult, args))
        {
            ErrorOutput.Write(
                [new TomixDiagnostic(
                    "TOMIX_CONFIG_CORRUPT",
                    DiagnosticSeverity.Error,
                    configLoadError.Message,
                    "Run 'tx config init --force' to reset the file, or repair it manually.")],
                parseResult.GetValue(GlobalOptions.ErrorFormat));
            return 2;
        }

        var exitCode = Invoke(parseResult);
        if (configLoadError is null)
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
        IReleaseSource? releaseSource = null,
        HttpClient? httpClient = null,
        string? configLoadError = null)
    {
        analyzer ??= new VpaxVertipaqAnalyzer(tokenProvider: null, version);
        var root = new RootCommand("tx - CLI for semantic models");
        foreach (var option in GlobalOptions.All())
            root.Options.Add(option);

        var mutations = services.Mutations;
        Func<CliConnectionState?> loadCurrentSession = services.LoadCurrentSession;

        var modules = new ICommandModule[]
        {
            new AddCommand(providers, services.State, mutations),
            new AuthCommand(services.ConfigStore, services.State),
            new BpaCommand(
                providers,
                services.State,
                mutations,
                services.BpaRules,
                services.ConfigDirectory,
                httpClient),
            new CompletionCommand(),
            new ConfigCommand(services.ConfigStore, services.ConfigDirectory, services.ConfigFilePath),
            new ConnectCommand(
                providers,
                workspaceCatalog ?? EmptyWorkspaceCatalog.Instance,
                cachedUsername ?? (() => null),
                services.State),
            new DeployCommand(providers, services.State, httpClient),
            new DepsCommand(providers, services.State),
            new DiffCommand(providers),
            new DoctorCommand(
                version,
                services.ConfigDirectory,
                services.ConfigStore,
                services.State,
                services.UpdateCheck,
                Path.Combine(services.ConfigDirectory, "auth", "auth-state.json"),
                providers.Select(provider => provider.GetType().Name).ToList(),
                configLoadError),
            new FindCommand(providers, services.State),
            new FormatCommand(providers, formatter, services.State, mutations),
            new GetCommand(providers, services.State),
            new IncrementalRefreshCommand(providers, services.State, mutations, loadCurrentSession),
            new InitCommand(),
            new LoadCommand(providers, services.State),
            new LsCommand(providers, services.State),
            new MvCommand(providers, services.State, mutations),
            new ProfileCommand(services.State),
            new QueryCommand(providers, loadCurrentSession),
            new RefreshCommand(providers, services.State, loadCurrentSession),
            new ReplaceCommand(providers, services.State, mutations),
            new RmCommand(providers, services.State, mutations),
            new SaveCommand(providers, services.State, httpClient),
            new ScriptCommand(providers, services.State, mutations),
            new SessionCommand(services.State),
            new SetCommand(providers, services.State, mutations),
            new StageCommand(providers, services.State, services.Staging),
            new TestCommand(providers, loadCurrentSession),
            new UpdateCommand(version, releaseSource ?? UnavailableReleaseSource.Instance, services.UpdateCheck),
            new ValidateCommand(providers, services.State),
            new VertipaqCommand(providers, analyzer, services.State, mutations)
        };

        foreach (var module in modules)
            root.Subcommands.Add(module.Build());

        ApplySpectreHelp(root);
        return root;
    }

    internal static bool CanRunWithCorruptConfig(ParseResult parseResult, IReadOnlyList<string> args)
    {
        if (args.Any(argument => argument is "--help" or "-h" or "-?"))
            return true;

        if (args.Contains("--version") && parseResult.CommandResult.Command is RootCommand)
            return true;

        var leaf = parseResult.CommandResult.Command.Name;
        if (leaf.Equals("doctor", StringComparison.OrdinalIgnoreCase))
            return true;

        var isConfig = args.Any(argument => argument.Equals("config", StringComparison.OrdinalIgnoreCase));
        return isConfig &&
               (leaf.Equals("paths", StringComparison.OrdinalIgnoreCase) ||
                leaf.Equals("init", StringComparison.OrdinalIgnoreCase) && args.Contains("--force"));
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

    /// <summary>Release source for contexts without network wiring (e.g. help-only test roots).</summary>
    private sealed class UnavailableReleaseSource : IReleaseSource
    {
        public static readonly UnavailableReleaseSource Instance = new();

        public Task<Tomix.Core.Update.ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken)
            => Task.FromResult<Tomix.Core.Update.ReleaseInfo?>(null);

        public Task<IReadOnlyList<Tomix.Core.Update.ReleaseInfo>> ListReleasesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<Tomix.Core.Update.ReleaseInfo>>([]);

        public Task<byte[]> DownloadAssetAsync(string version, string assetName, CancellationToken cancellationToken)
            => throw new HttpRequestException("No release source is configured.");

        public Task<string> DownloadChecksumsAsync(string version, CancellationToken cancellationToken)
            => throw new HttpRequestException("No release source is configured.");
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
