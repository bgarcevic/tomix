using System.Runtime.InteropServices;
using System.Text.Json;
using Tomix.App.Config;
using Tomix.App.State;
using Tomix.App.Update;
using Tomix.Core.Doctor;
using Tomix.Core.Results;
using Tomix.Core.Update;

namespace Tomix.App.Doctor;

/// <summary>
/// Runs deterministic local health checks. It never authenticates, opens a credential store,
/// refreshes a token, or contacts a release/model service.
/// </summary>
public sealed class DoctorHandler
{
    private readonly string _configDirectory;
    private readonly TomixConfigStore _configStore;
    private readonly CliStateStore _state;
    private readonly UpdateCheckStore _updateStore;
    private readonly string _authMetadataFile;
    private readonly IReadOnlyList<string> _providerNames;
    private readonly string? _configLoadError;

    public DoctorHandler(
        string configDirectory,
        TomixConfigStore configStore,
        CliStateStore state,
        UpdateCheckStore updateStore,
        string authMetadataFile,
        IReadOnlyList<string> providerNames,
        string? configLoadError = null)
    {
        _configDirectory = configDirectory;
        _configStore = configStore;
        _state = state;
        _updateStore = updateStore;
        _authMetadataFile = authMetadataFile;
        _providerNames = providerNames;
        _configLoadError = configLoadError;
    }

    public TomixResult<DoctorResult> Handle(string version, DoctorTerminalCapabilities terminal)
    {
        var checks = new List<DoctorCheck>
        {
            new("runtime", DoctorCheckStatus.Pass, $".NET {Environment.Version}"),
            new("operating-system", DoctorCheckStatus.Pass, RuntimeInformation.OSDescription)
        };

        AddConfigDirectoryCheck(checks);
        AddConfigCheck(checks);
        AddProfilesCheck(checks);
        AddSessionsCheck(checks);
        AddAuthenticationCheck(checks);
        AddProvidersCheck(checks);
        checks.Add(new DoctorCheck(
            "terminal",
            DoctorCheckStatus.Pass,
            $"interactive={terminal.Interactive}, ansi={terminal.Ansi}, color={terminal.ColorSystem}"));
        var latestVersion = AddCachedUpdateCheck(checks, version);

        var result = new DoctorResult(
            version,
            RuntimeInformation.OSDescription,
            Environment.Version.ToString(),
            _configDirectory,
            terminal,
            checks,
            latestVersion);
        var failed = checks.Any(check => check.Status == DoctorCheckStatus.Fail);

        return new TomixResult<DoctorResult>(
            Success: !failed,
            Data: result,
            Diagnostics: [],
            ExitCode: failed ? 1 : 0);
    }

    private void AddConfigDirectoryCheck(List<DoctorCheck> checks)
    {
        string? probe = null;
        try
        {
            Directory.CreateDirectory(_configDirectory);
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

    private void AddProfilesCheck(List<DoctorCheck> checks)
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
                ? new DoctorCheck("profiles", DoctorCheckStatus.Fail, $"profile(s) without a usable target: {string.Join(", ", invalid)}")
                : new DoctorCheck(
                    "profiles",
                    profiles.Count == 0 ? DoctorCheckStatus.Warning : DoctorCheckStatus.Pass,
                    profiles.Count == 0 ? "no profiles configured" : $"valid ({profiles.Count} profile(s))"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            checks.Add(new DoctorCheck("profiles", DoctorCheckStatus.Fail, ex.Message));
        }
    }

    private void AddSessionsCheck(List<DoctorCheck> checks)
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

            checks.Add(invalid.Count == 0
                ? new DoctorCheck("sessions", DoctorCheckStatus.Pass, $"valid ({sessions.Count} session(s))")
                : new DoctorCheck("sessions", DoctorCheckStatus.Fail, $"invalid session file(s): {string.Join(", ", invalid)}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            checks.Add(new DoctorCheck("sessions", DoctorCheckStatus.Fail, ex.Message));
        }
    }

    private void AddAuthenticationCheck(List<DoctorCheck> checks)
    {
        if (!File.Exists(_authMetadataFile))
        {
            checks.Add(new DoctorCheck("authentication", DoctorCheckStatus.Warning, "no cached authentication metadata"));
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_authMetadataFile));
            var username = document.RootElement.EnumerateObject()
                .FirstOrDefault(property => property.Name.Equals("username", StringComparison.OrdinalIgnoreCase))
                .Value;
            if (username.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(username.GetString()))
                throw new JsonException("username is missing");

            checks.Add(new DoctorCheck("authentication", DoctorCheckStatus.Pass, $"cached metadata for {username.GetString()}"));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            checks.Add(new DoctorCheck("authentication", DoctorCheckStatus.Fail, $"cached metadata is invalid: {ex.Message}"));
        }
    }

    private void AddProvidersCheck(List<DoctorCheck> checks)
        => checks.Add(_providerNames.Count == 0
            ? new DoctorCheck("model-providers", DoctorCheckStatus.Fail, "no model providers registered")
            : new DoctorCheck("model-providers", DoctorCheckStatus.Pass, string.Join(", ", _providerNames)));

    private string? AddCachedUpdateCheck(List<DoctorCheck> checks, string version)
    {
        var cached = _updateStore.Load();
        if (cached is null)
        {
            checks.Add(new DoctorCheck("update-cache", DoctorCheckStatus.Warning, "no cached update information"));
            return null;
        }

        var newer = CliVersion.TryParse(cached.LatestVersion, out var latest) &&
                    CliVersion.TryParse(version, out var current) &&
                    latest.IsNewerThan(current);
        checks.Add(new DoctorCheck(
            "update-cache",
            newer ? DoctorCheckStatus.Warning : DoctorCheckStatus.Pass,
            newer
                ? $"cached update available: {cached.LatestVersion} (checked {cached.LastCheckedUtc:O})"
                : $"cached latest: {cached.LatestVersion} (checked {cached.LastCheckedUtc:O})"));
        return cached.LatestVersion;
    }
}
