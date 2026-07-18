namespace Tomix.Core.Update;

/// <summary>How the running CLI was installed, which decides how it can be updated.</summary>
public enum InstallKind
{
    /// <summary>Installed with <c>dotnet tool install -g Tomix.Cli</c>; updated via <c>dotnet tool update</c>.</summary>
    DotnetTool,

    /// <summary>Self-contained binary from a GitHub Release (install.sh/install.ps1); updated by swapping the binary.</summary>
    Standalone,

    /// <summary>Running from a build output directory (e.g. the <c>./tx</c> dev wrapper); never updated in place.</summary>
    Development,

    Unknown
}
