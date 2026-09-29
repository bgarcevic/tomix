using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tomix.App.Config;
using Tomix.App.State;
using Tomix.App.Update;
using Tomix.Core.Authentication;
using Tomix.Core.Doctor;
using Tomix.Core.Results;
using Tomix.Core.Update;
using Tomix.Platform.Configuration;

namespace Tomix.App.Doctor;

/// <summary>
/// Runs deterministic local health checks. It never authenticates, opens a credential store,
/// refreshes a token, or contacts a release/model service, and it never creates or changes
/// tomix state beyond a short-lived probe file in an existing config directory.
/// <para>
/// The report is meant to be pasted into bug reports, so by default it leaves out personal
/// details: the home directory becomes <c>~</c>, and account names, server, database, model,
/// profile and session names are replaced by what kind of thing they are. <c>showDetails</c>
/// keeps them.
/// </para>
/// </summary>
public sealed class DoctorHandler
{
    private readonly string _configDirectory;
    private readonly TomixConfigStore _configStore;
    private readonly CliStateStore _state;
    private readonly UpdateCheckStore _updateStore;
    private readonly string _authDirectory;
    private readonly bool _tokenCacheOnDisk;
    private readonly InstallKind _installKind;
    private readonly string? _configLoadError;
    private readonly string? _homeDirectory;

    /// <param name="tokenCacheOnDisk">
    /// Whether the MSAL token cache lives in a file in <paramref name="authDirectory"/>. True on
    /// Windows (DPAPI-protected file); macOS and Linux keep it in the OS keychain/keyring, which
    /// doctor must not open. Defaults to the current platform.
    /// </param>
    /// <param name="installKind">How the running CLI was installed; defaults to detecting it.</param>
    /// <param name="homeDirectory">The directory shown as <c>~</c>; defaults to the user profile.</param>
    public DoctorHandler(
        string configDirectory,
        TomixConfigStore configStore,
        CliStateStore state,
        UpdateCheckStore updateStore,
        string authDirectory,
        string? configLoadError = null,
        bool? tokenCacheOnDisk = null,
        InstallKind? installKind = null,
        string? homeDirectory = null)
    {
        _configDirectory = configDirectory;
        _configStore = configStore;
        _state = state;
        _updateStore = updateStore;
        _authDirectory = authDirectory;
        _configLoadError = configLoadError;
        _tokenCacheOnDisk = tokenCacheOnDisk ?? OperatingSystem.IsWindows();
        _installKind = installKind ?? InstallationInspector.Detect();
        _homeDirectory = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public TomixResult<DoctorResult> Handle(string version, DoctorTerminalCapabilities terminal, bool showDetails = false)
    {
        var checks = new List<DoctorCheck>();

        AddConfigDirectoryCheck(checks);
        AddConfigCheck(checks);
        AddProfilesCheck(checks, showDetails);
        AddSessionsCheck(checks, showDetails);
        AddCurrentSessionCheck(checks, showDetails);
        AddAuthenticationCheck(checks, showDetails);
        var latestVersion = AddCachedUpdateCheck(checks, version);

        if (!showDetails)
            checks = checks.Select(check => check with { Message = HideHomeDirectory(check.Message) }).ToList();

        var result = new DoctorResult(
            version,
            RuntimeInformation.OSDescription,
            Environment.Version.ToString(),
            showDetails ? _configDirectory : HideHomeDirectory(_configDirectory),
            terminal,
            checks,
            latestVersion,
            _installKind);
        var failed = checks.Any(check => check.Status == DoctorCheckStatus.Fail);

        return new TomixResult<DoctorResult>(
            Success: !failed,
            Data: result,
            Diagnostics: [],
            ExitCode: failed ? 1 : 0);
    }

    private void AddConfigDirectoryCheck(List<DoctorCheck> checks)
    {
        if (File.Exists(_configDirectory))
        {
            checks.Add(new DoctorCheck("config-directory", DoctorCheckStatus.Fail, $"{_configDirectory} is a file, not a directory"));
            return;
        }

        if (!Directory.Exists(_configDirectory))
        {
            // Creating it here would make doctor the command that changes a fresh machine.
            checks.Add(new DoctorCheck("config-directory", DoctorCheckStatus.Info, $"not created yet; created on first use: {_configDirectory}"));
            return;
        }

        string? probe = null;
        try
        {
            _ = Directory.EnumerateFileSystemEntries(_configDirectory).Take(1).ToList();
            probe = Path.Combine(_configDirectory, $".doctor-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "tomix doctor");
            checks.Add(new DoctorCheck("config-directory", DoctorCheckStatus.Pass, $"read/write: {_configDirectory}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            checks.Add(new DoctorCheck("config-directory", DoctorCheckStatus.Fail, $"read/write check failed: {ex.Message}"));
        }
        finally
        {
            if (probe is not null)
            {
                try { File.Delete(probe); }
                catch (Exception) { /* the failed cleanup is covered by the write-access check */ }
            }
        }
    }

    private void AddConfigCheck(List<DoctorCheck> checks)
    {
        if (_configLoadError is not null)
        {
            checks.Add(new DoctorCheck("configuration", DoctorCheckStatus.Fail, _configLoadError));
            return;
        }

        try
        {
            var values = _configStore.Load();
            checks.Add(new DoctorCheck("configuration", DoctorCheckStatus.Pass, $"valid ({values.Count} value(s))"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            checks.Add(new DoctorCheck("configuration", DoctorCheckStatus.Fail, ex.Message));
        }
    }

    private void AddProfilesCheck(List<DoctorCheck> checks, bool showDetails)
    {
        try
        {
            var profiles = _state.LoadProfiles();
            var invalid = profiles
                .Where(pair => string.IsNullOrWhiteSpace(pair.Value.Server) &&
                               string.IsNullOrWhiteSpace(pair.Value.Model) &&
                               !pair.Value.Local)
                .Select(pair => pair.Key)
                .ToList();
            checks.Add(invalid.Count > 0
                ? new DoctorCheck(
                    "profiles",
                    DoctorCheckStatus.Fail,
                    showDetails
                        ? $"profile(s) without a usable target: {string.Join(", ", invalid)}"
                        : $"{invalid.Count} profile(s) without a usable target; run with --show-details to name them")
                : profiles.Count == 0
                    ? new DoctorCheck("profiles", DoctorCheckStatus.Info, "no profiles configured")
                    : new DoctorCheck("profiles", DoctorCheckStatus.Pass, $"valid ({profiles.Count} profile(s))"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            checks.Add(new DoctorCheck("profiles", DoctorCheckStatus.Fail, ex.Message));
        }
    }

    private void AddSessionsCheck(List<DoctorCheck> checks, bool showDetails)
    {
        try
        {
            var sessions = _state.ListSessions();
            var invalid = new List<string>();
            foreach (var session in sessions)
            {
                try
                {
                    var json = File.ReadAllText(session.Path);
                    var state = JsonSerializer.Deserialize<CliConnectionState>(json);
                    if (state is null ||
                        string.IsNullOrWhiteSpace(state.Server) &&
                        string.IsNullOrWhiteSpace(state.Model) &&
                        !state.Local)
                        invalid.Add(session.SessionId);
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    invalid.Add(session.SessionId);
                }
            }

            if (invalid.Count > 0)
            {
                checks.Add(new DoctorCheck(
                    "sessions",
                    DoctorCheckStatus.Fail,
                    showDetails
                        ? $"invalid session file(s): {string.Join(", ", invalid)}"
                        : $"{invalid.Count} invalid session file(s); run with --show-details to name them"));
                return;
            }

            // Only sessions of exited shells are known to be stale: a directory session's file
            // name holds a hash of its directory, not the path, so a deleted folder is undetectable.
            var stale = _state.SelectPruneCandidates(all: false).Count;
            checks.Add(stale == 0
                ? new DoctorCheck("sessions", DoctorCheckStatus.Pass, $"valid ({sessions.Count} session(s))")
                : new DoctorCheck(
                    "sessions",
                    DoctorCheckStatus.Info,
                    $"valid ({sessions.Count} session(s), {stale} from exited shells; run 'tx session prune')"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            checks.Add(new DoctorCheck("sessions", DoctorCheckStatus.Fail, ex.Message));
        }
    }

    private void AddCurrentSessionCheck(List<DoctorCheck> checks, bool showDetails)
    {
        var id = showDetails ? _state.CurrentSessionId : $"{_state.CurrentSessionKind} session";
        CliConnectionState? state;
        try
        {
            state = _state.LoadCurrentSession();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            checks.Add(new DoctorCheck("current-session", DoctorCheckStatus.Fail, $"{id}: {ex.Message}"));
            return;
        }

        var target = state is null ? null : showDetails ? DescribeTarget(state) : DescribeTargetKind(state);
        checks.Add(target is null
            ? new DoctorCheck("current-session", DoctorCheckStatus.Info, $"{id}: not connected")
            : new DoctorCheck("current-session", DoctorCheckStatus.Pass, $"{id}: {target}"));
    }

    private static string? DescribeTarget(CliConnectionState state)
    {
        string? target;
        if (!string.IsNullOrWhiteSpace(state.Model))
            target = $"model {state.Model}";
        else if (!string.IsNullOrWhiteSpace(state.Server))
            target = string.IsNullOrWhiteSpace(state.Database)
                ? $"server {state.Server}"
                : $"server {state.Server}, database {state.Database}";
        else
            return null;

        return string.IsNullOrWhiteSpace(state.Profile) ? target : $"{target} (profile {state.Profile})";
    }

    /// <summary>What a connection points at, without naming it.</summary>
    private static string? DescribeTargetKind(CliConnectionState state)
    {
        if (!string.IsNullOrWhiteSpace(state.Model))
            return "local model files";
        if (string.IsNullOrWhiteSpace(state.Server))
            return null;

        var server = state.Server.Trim();
        if (server.StartsWith("powerbi://", StringComparison.OrdinalIgnoreCase))
            return "Power BI / Fabric workspace";
        if (server.StartsWith("asazure://", StringComparison.OrdinalIgnoreCase))
            return "Azure Analysis Services server";

        var host = server.StartsWith('[') ? server[..(server.IndexOf(']') + 1)] : server.Split(':', 2)[0];
        return host.ToLowerInvariant() is "localhost" or "127.0.0.1" or "[::1]" or "."
            ? "local Analysis Services instance"
            : "Analysis Services server";
    }

    private string HideHomeDirectory(string text)
    {
        if (string.IsNullOrWhiteSpace(_homeDirectory))
            return text;

        // Only a whole path segment: C:\Users\bob must not rewrite C:\Users\bobby.
        var home = Regex.Escape(Path.TrimEndingDirectorySeparator(_homeDirectory));
        var options = OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None;
        return Regex.Replace(text, $@"{home}(?![^\\/])", "~", options);
    }

    private void AddAuthenticationCheck(List<DoctorCheck> checks, bool showDetails)
    {
        var metadataFile = Path.Combine(_authDirectory, TomixPaths.AuthStateFileName);
        if (!File.Exists(metadataFile))
        {
            checks.Add(new DoctorCheck("authentication", DoctorCheckStatus.Info, "not signed in"));
            return;
        }

        string username;
        AuthMethod? method;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataFile));
            var usernameElement = Property(document.RootElement, "username");
            if (usernameElement is not { ValueKind: JsonValueKind.String } ||
                string.IsNullOrWhiteSpace(usernameElement.Value.GetString()))
                throw new JsonException("username is missing");

            username = usernameElement.Value.GetString()!;
            method = Property(document.RootElement, "method") is { ValueKind: JsonValueKind.String } methodElement &&
                     Enum.TryParse<AuthMethod>(methodElement.GetString(), ignoreCase: true, out var parsed)
                ? parsed
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            checks.Add(new DoctorCheck("authentication", DoctorCheckStatus.Fail, $"cached metadata is invalid: {ex.Message}"));
            return;
        }

        var signedIn = (showDetails, method) switch
        {
            (true, null) => $"signed in as {username}",
            (true, _) => $"signed in as {username} ({method})",
            (false, null) => "signed in",
            (false, _) => $"signed in ({method})"
        };
        var cacheFile = method switch
        {
            AuthMethod.Interactive or AuthMethod.DeviceCode => TomixPaths.AuthUserCacheFileName,
            AuthMethod.ServicePrincipalSecret or AuthMethod.ServicePrincipalCertificate => TomixPaths.AuthAppCacheFileName,
            _ => null
        };

        checks.Add(_tokenCacheOnDisk && cacheFile is not null && !File.Exists(Path.Combine(_authDirectory, cacheFile))
            ? new DoctorCheck(
                "authentication",
                DoctorCheckStatus.Warning,
                $"{signedIn}, but the token cache is missing; run 'tx auth login'")
            : new DoctorCheck("authentication", DoctorCheckStatus.Pass, signedIn));
    }

    private static JsonElement? Property(JsonElement element, string name)
        => element.EnumerateObject()
            .Where(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(property => (JsonElement?)property.Value)
            .FirstOrDefault();

    private string? AddCachedUpdateCheck(List<DoctorCheck> checks, string version)
    {
        var cached = _updateStore.Load();
        if (cached is null)
        {
            checks.Add(new DoctorCheck("update-cache", DoctorCheckStatus.Info, "not checked yet"));
            return null;
        }

        var checkedAt = cached.LastCheckedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        var newer = CliVersion.TryParse(cached.LatestVersion, out var latest) &&
                    CliVersion.TryParse(version, out var current) &&
                    latest.IsNewerThan(current);
        if (!newer)
            checks.Add(new DoctorCheck("update-cache", DoctorCheckStatus.Pass, $"up to date (checked {checkedAt})"));
        else if (_installKind is InstallKind.DotnetTool or InstallKind.Standalone)
            checks.Add(new DoctorCheck(
                "update-cache",
                DoctorCheckStatus.Warning,
                $"{cached.LatestVersion} is available (checked {checkedAt}); run 'tx update'"));
        else
            // A source build (or an install tx cannot update) is not something 'tx update' can fix,
            // matching the update notice, which stays silent for these installs.
            checks.Add(new DoctorCheck(
                "update-cache",
                DoctorCheckStatus.Info,
                $"latest release is {cached.LatestVersion} (checked {checkedAt}); this build cannot be updated with 'tx update'"));
        return cached.LatestVersion;
    }
}
