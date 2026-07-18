using Tomix.Core.Update;

namespace Tomix.App.Update;

/// <summary>Detects how the running CLI was installed by inspecting process and base paths.</summary>
public static class InstallationInspector
{
    public static InstallKind Detect()
        => DetectFromPaths(Environment.ProcessPath, AppContext.BaseDirectory, Directory.Exists);

    internal static InstallKind DetectFromPaths(
        string? processPath,
        string baseDirectory,
        Func<string, bool> directoryExists)
    {
        if (string.IsNullOrWhiteSpace(processPath))
            return InstallKind.Unknown;

        // The ./tx dev wrapper runs via `dotnet run`, so the app loads from bin/<Configuration>.
        if (ContainsSegments(baseDirectory, "bin", "Debug") || ContainsSegments(baseDirectory, "bin", "Release"))
            return InstallKind.Development;

        // Global-tool layout: the shim at ~/.dotnet/tools/tx runs the payload from
        // ~/.dotnet/tools/.store/tomix.cli/<version>/..., so either signal identifies it.
        var shimDirectory = Path.GetDirectoryName(processPath);
        if (shimDirectory is not null && directoryExists(Path.Combine(shimDirectory, ".store", "tomix.cli")))
            return InstallKind.DotnetTool;
        if (ContainsSegments(baseDirectory, ".store", "tomix.cli") || ContainsSegments(processPath, ".dotnet", "tools"))
            return InstallKind.DotnetTool;

        // Split on both separators so the same logic holds for Windows paths in tests
        // running on Unix (Path.GetFileName only honors the native separator).
        var fileName = processPath.Split('/', '\\')[^1];
        if (string.Equals(fileName, "tx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "tx.exe", StringComparison.OrdinalIgnoreCase))
        {
            return InstallKind.Standalone;
        }

        return InstallKind.Unknown;
    }

    private static bool ContainsSegments(string path, string first, string second)
    {
        var segments = path.Split('/', '\\');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], first, StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[i + 1], second, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
