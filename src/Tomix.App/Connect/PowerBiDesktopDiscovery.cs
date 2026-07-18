namespace Tomix.App.Connect;

/// <summary>
/// Discovers running Power BI Desktop instances by scanning the known AnalysisServices
/// workspace roots for <c>msmdsrv.port.txt</c> files, yielding <c>localhost:&lt;port&gt;</c>
/// endpoints usable as XMLA targets.
/// </summary>
public static class PowerBiDesktopDiscovery
{
    public static IReadOnlyList<string> DiscoverEndpoints(IEnumerable<string>? roots = null)
    {
        var existingRoots = (roots ?? WorkspaceRoots())
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var endpoints = new List<string>();

        foreach (var root in existingRoots)
        {
            foreach (var portFile in Directory.EnumerateFiles(root, "msmdsrv.port.txt", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(portFile).Trim();
                if (int.TryParse(text, out var port) && port > 0)
                    endpoints.Add($"localhost:{port}");
            }
        }

        return endpoints.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> WorkspaceRoots()
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(localAppData))
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Microsoft", "Power BI Desktop", "AnalysisServicesWorkspaces");
            yield return Path.Combine(localAppData, "Packages", "Microsoft.MicrosoftPowerBIDesktop_8wekyb3d8bbwe", "LocalCache", "Microsoft", "Power BI Desktop", "AnalysisServicesWorkspaces");
        }
    }
}
