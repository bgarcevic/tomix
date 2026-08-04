using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Tomix.App.Connect;

/// <summary>A running <c>msmdsrv.exe</c>, joined to the Desktop window hosting it.</summary>
/// <param name="DataDirectory">
/// The engine's data directory, from the <c>-s</c> argument. This is the folder holding
/// <c>msmdsrv.port.txt</c>, so it joins a discovered port file to its report.
/// </param>
/// <param name="ReportName">
/// The hosting Power BI Desktop window title, which is the only human-meaningful name available:
/// over XMLA a Desktop database is named by a GUID and its model is always literally "Model".
/// Null when the parent has no window (starting up, or already gone).
/// </param>
internal sealed record DesktopProcess(string DataDirectory, string? ReportName);

/// <summary>
/// Enumerates running Power BI Desktop engines so discovered endpoints can be labelled with a
/// report name. Windows-only and best-effort: every failure yields an empty list, leaving
/// <see cref="PowerBiDesktopDiscovery"/> to fall back to bare <c>localhost:&lt;port&gt;</c> labels.
/// </summary>
internal static class PowerBiDesktopProcesses
{
    public static IReadOnlyList<DesktopProcess> Describe()
        => OperatingSystem.IsWindows() ? DescribeWindows() : [];

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<DesktopProcess> DescribeWindows()
    {
        try
        {
            // WMI is the only supported way to read another process's command line; the workspace
            // path is not derivable from the process object alone.
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, CommandLine FROM Win32_Process WHERE Name = 'msmdsrv.exe'");

            var processes = new List<DesktopProcess>();
            foreach (var row in searcher.Get())
            {
                using var process = (ManagementObject)row;
                if (DataDirectoryFrom(process["CommandLine"] as string) is not { } dataDirectory)
                    continue;

                processes.Add(new DesktopProcess(dataDirectory, WindowTitle(process["ParentProcessId"])));
            }

            return processes;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
            // WMI disabled, the service stopped, or access denied. Labels are a nicety; discovery
            // must still work without them.
            return [];
        }
    }

    /// <summary>
    /// Extracts the quoted path following <c>-s</c> from an msmdsrv command line, e.g.
    /// <c>msmdsrv.exe -c -n AnalysisServicesWorkspace_&lt;guid&gt; -s "C:\...\Workspace_&lt;guid&gt;\Data"</c>.
    /// </summary>
    internal static string? DataDirectoryFrom(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;

        var flag = commandLine.IndexOf(" -s ", StringComparison.Ordinal);
        if (flag < 0)
            return null;

        var rest = commandLine.AsSpan(flag + " -s ".Length).TrimStart();
        if (rest.IsEmpty)
            return null;

        ReadOnlySpan<char> path;
        if (rest[0] == '"')
        {
            // Quoted, which is the case for every real install since the path contains spaces.
            rest = rest[1..];
            var closing = rest.IndexOf('"');
            if (closing < 0)
                return null;

            path = rest[..closing];
        }
        else
        {
            var space = rest.IndexOf(' ');
            path = space < 0 ? rest : rest[..space];
        }

        return path.IsEmpty ? null : path.ToString();
    }

    [SupportedOSPlatform("windows")]
    private static string? WindowTitle(object? parentProcessId)
    {
        if (parentProcessId is not uint pid)
            return null;

        try
        {
            using var parent = Process.GetProcessById((int)pid);
            var title = parent.MainWindowTitle;
            return string.IsNullOrWhiteSpace(title) ? null : title;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Parent already exited, or has no window yet.
            return null;
        }
    }
}
