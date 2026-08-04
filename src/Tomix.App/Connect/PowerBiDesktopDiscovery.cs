using System.Net.NetworkInformation;

namespace Tomix.App.Connect;

/// <summary>A running Power BI Desktop instance available as an XMLA target.</summary>
/// <param name="Endpoint"><c>localhost:&lt;port&gt;</c>, usable directly as a server.</param>
/// <param name="ReportName">
/// The Desktop window title, or null when it could not be read. This is the only human-meaningful
/// label available: over XMLA a Desktop database is named by a GUID and its model is always
/// literally "Model", so neither can tell two instances apart for a user.
/// </param>
/// <param name="PortFile">
/// The <c>msmdsrv.port.txt</c> this instance was found through. Persisted alongside a cached
/// <paramref name="ReportName"/> so the name can later be revalidated without a WMI query.
/// </param>
public sealed record PowerBiDesktopInstance(string Endpoint, string? ReportName, string PortFile);

/// <summary>
/// Discovers running Power BI Desktop instances by probing the known AnalysisServices workspace
/// roots for <c>msmdsrv.port.txt</c>, yielding <c>localhost:&lt;port&gt;</c> endpoints usable as
/// XMLA targets.
/// </summary>
/// <remarks>
/// Two properties of the on-disk layout are easy to get wrong, and either one alone makes
/// discovery find nothing at all:
/// <list type="bullet">
/// <item>The workspace root differs per install variant. Microsoft Store builds use
/// <c>%USERPROFILE%\Microsoft\Power BI Desktop Store App\...</c> — a different base folder
/// <em>and</em> a different product folder than the MSI layout. Verified against the <c>-s</c>
/// argument of a running <c>msmdsrv.exe</c> (Store install, 2.156).</item>
/// <item>The port file is UTF-16LE with <em>no BOM</em>, so <c>File.ReadAllText</c> (UTF-8 with
/// BOM detection) decodes the bytes <c>36 00 31 00 ...</c> into digits interleaved with NUL
/// characters and <c>int.TryParse</c> returns false. Ports are parsed from raw bytes instead.</item>
/// </list>
/// </remarks>
public static class PowerBiDesktopDiscovery
{
    private const string PortFileName = "msmdsrv.port.txt";

    /// <summary>
    /// The Desktop instances that are currently listening. Stale port files (msmdsrv does not
    /// reliably delete them on shutdown) are filtered out.
    /// </summary>
    public static IReadOnlyList<PowerBiDesktopInstance> DiscoverInstances(IEnumerable<string>? roots = null)
        => DiscoverInstances(roots ?? WorkspaceRoots(), TryReadPort, IsPortListening, PowerBiDesktopProcesses.Describe);

    /// <param name="isPortListening">
    /// Liveness probe, or <c>null</c> to skip liveness filtering so tests stay offline.
    /// </param>
    /// <param name="describeProcesses">
    /// Supplies report names for the discovered ports. Best-effort: when it returns nothing the
    /// instances are still reported, just unlabelled.
    /// </param>
    internal static IReadOnlyList<PowerBiDesktopInstance> DiscoverInstances(
        IEnumerable<string> roots,
        Func<string, int?> readPort,
        Func<int, bool>? isPortListening,
        Func<IReadOnlyList<DesktopProcess>> describeProcesses)
    {
        var instances = new List<PowerBiDesktopInstance>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Keyed by the engine's data directory, which is also the folder holding the port file.
        var reportNames = describeProcesses()
            .GroupBy(p => NormalizeDirectory(p.DataDirectory), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().ReportName, StringComparer.OrdinalIgnoreCase);

        var distinctRoots = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in distinctRoots)
        {
            foreach (var portFile in PortFiles(root))
            {
                if (readPort(portFile) is not { } port)
                    continue;
                if (isPortListening is not null && !isPortListening(port))
                    continue;

                var endpoint = $"localhost:{port}";
                if (!seen.Add(endpoint))
                    continue;

                var directory = NormalizeDirectory(Path.GetDirectoryName(portFile));
                instances.Add(new PowerBiDesktopInstance(
                    endpoint,
                    reportNames.GetValueOrDefault(directory),
                    portFile));
            }
        }

        return instances;
    }

    private static string NormalizeDirectory(string? path)
        => string.IsNullOrWhiteSpace(path) ? "" : Path.TrimEndingDirectorySeparator(path);

    /// <summary>
    /// Whether the instance a cached report name was read from is still the live instance on
    /// <paramref name="endpoint"/>. Two conditions, and both are needed:
    /// <list type="bullet">
    /// <item><paramref name="portFile"/> still holds that port. Power BI creates a fresh workspace
    /// folder per session, so this is what distinguishes the original instance from a different
    /// report that has since been assigned the same port.</item>
    /// <item>Something is still listening there — msmdsrv does not reliably delete its port file on
    /// shutdown, so the file alone would keep labelling an instance the user has closed. This is
    /// the same staleness that <see cref="DiscoverInstances(IEnumerable{string}?)"/> filters, and
    /// the two must agree.</item>
    /// </list>
    /// Cheap by design — a small file read and a listener-table lookup, so showing a connection
    /// does not need the ~220ms WMI lookup.
    /// </summary>
    public static bool StillServes(string? portFile, string? endpoint)
        => StillServes(portFile, endpoint, TryReadPort, IsPortListening);

    internal static bool StillServes(
        string? portFile,
        string? endpoint,
        Func<string, int?> readPort,
        Func<int, bool> isPortListening)
    {
        if (string.IsNullOrWhiteSpace(portFile) || string.IsNullOrWhiteSpace(endpoint))
            return false;

        var separator = endpoint.LastIndexOf(':');
        if (separator < 0 || !int.TryParse(endpoint.AsSpan(separator + 1), out var expected))
            return false;

        return readPort(portFile) == expected && isPortListening(expected);
    }

    // Bounded probe. The port file lives at
    // <root>\AnalysisServicesWorkspace_<guid>\Data\msmdsrv.port.txt at a fixed depth, so a
    // recursive search would walk the entire model data cache (large, and partly ACL-restricted)
    // for no gain. The workspace-root candidate covers older layouts that omitted "Data".
    private static IEnumerable<string> PortFiles(string root)
    {
        foreach (var workspace in SafeGetDirectories(root))
        {
            string[] candidates =
            [
                Path.Combine(workspace, "Data", PortFileName),
                Path.Combine(workspace, PortFileName)
            ];

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    yield return candidate;
                    break; // one msmdsrv instance per workspace
                }
            }
        }
    }

    private static string[] SafeGetDirectories(string root)
    {
        try
        {
            return Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A root that does not exist (DirectoryNotFoundException), lives on a disconnected
            // drive, denies listing, or is a malformed path contributes no instances.
            // `tx connect --local` must report "none found", never throw.
            return [];
        }
    }

    internal static int? TryReadPort(string portFile)
    {
        try
        {
            return ParsePort(File.ReadAllBytes(portFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked while msmdsrv writes it, or the workspace was torn down mid-probe.
            // Either way this instance is not usable right now.
            return null;
        }
    }

    /// <summary>
    /// Parses a TCP port from raw port-file bytes. Digits are collected and padding/BOM bytes
    /// skipped, which decodes UTF-16LE (with or without BOM), UTF-16BE, and plain UTF-8/ASCII
    /// without assuming every Power BI build writes the same encoding. Returns <c>null</c> for
    /// empty, non-numeric, zero, negative, or out-of-range content.
    /// </summary>
    internal static int? ParsePort(ReadOnlySpan<byte> bytes)
    {
        var port = 0;
        var digits = 0;

        foreach (var b in bytes)
        {
            switch (b)
            {
                // UTF-16 padding bytes, UTF-8/UTF-16 BOM bytes, and surrounding whitespace.
                case 0 or 0xEF or 0xBB or 0xBF or 0xFE or 0xFF:
                case (byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t':
                    continue;
                case >= (byte)'0' and <= (byte)'9':
                    if (++digits > 5) // 65535 is the widest valid port
                        return null;
                    port = (port * 10) + (b - '0');
                    continue;
                default:
                    return null; // not a bare port number (e.g. a '-' sign or stray text)
            }
        }

        return digits > 0 && port is > 0 and <= 65535 ? port : null;
    }

    /// <summary>
    /// True when something is listening on the loopback port. msmdsrv does not reliably delete
    /// its port file on shutdown, so without this a stale file yields a dead endpoint that fails
    /// much later in <c>InfoModelHandler</c> with a connect error that reads like an auth or
    /// provider problem. Fails <em>open</em>: if the listener table is unavailable we return the
    /// instance rather than hide it.
    /// </summary>
    private static bool IsPortListening(int port)
    {
        try
        {
            foreach (var listener in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            {
                if (listener.Port == port)
                    return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return true;
        }
    }

    /// <summary>
    /// The AnalysisServices workspace roots, one per known install variant. Power BI Desktop keeps
    /// its workspaces somewhere different depending on how it was installed, so every variant has
    /// to be probed — dropping one silently blinds <c>--local</c> to that install type.
    /// </summary>
    internal static IEnumerable<string> WorkspaceRoots()
    {
        const string StoreProduct = "Power BI Desktop Store App";
        const string MsiProduct = "Power BI Desktop";

        // Microsoft Store. Note the base is the user profile, not LOCALAPPDATA.
        var userProfile = ResolveFolder("USERPROFILE", Environment.SpecialFolder.UserProfile);
        if (userProfile is not null)
            yield return Workspaces(userProfile, StoreProduct);

        var localAppData = ResolveFolder("LOCALAPPDATA", Environment.SpecialFolder.LocalApplicationData);
        if (localAppData is not null)
        {
            // Reported Store variant.
            yield return Workspaces(localAppData, StoreProduct);

            // MSI / Download Center install.
            yield return Workspaces(localAppData, MsiProduct);

            // Legacy Store packaging.
            yield return Path.Combine(
                localAppData, "Packages", "Microsoft.MicrosoftPowerBIDesktop_8wekyb3d8bbwe",
                "LocalCache", "Microsoft", MsiProduct, "AnalysisServicesWorkspaces");
        }

        static string Workspaces(string baseFolder, string product)
            => Path.Combine(baseFolder, "Microsoft", product, "AnalysisServicesWorkspaces");
    }

    private static string? ResolveFolder(string variable, Environment.SpecialFolder fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value))
            value = Environment.GetFolderPath(fallback);

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
