using Mdl.Core.Results;

namespace Mdl.Cli.Output;

/// <summary>
/// Shared bridge between handler results and the console: validates the format option,
/// renders either JSON or a command-specific human view, prints diagnostics to stderr,
/// and maps the result to an exit code.
/// </summary>
internal static class CommandOutput
{
    /// <summary>
    /// Validates <paramref name="format"/>, writing an error to stderr if it is unrecognised.
    /// Returns <c>false</c> so the command can exit with code 2 (invalid arguments).
    /// </summary>
    public static bool TryValidateFormat(string format)
    {
        if (OutputFormats.IsValid(format))
            return true;

        Console.Error.WriteLine("Invalid --format value. Expected: human or json.");
        return false;
    }

    /// <summary>
    /// Renders a successful result (JSON or human) or prints its diagnostics, returning the exit code.
    /// Branches on <c>Data</c> rather than <c>Success</c> so commands like <c>doctor</c> can still
    /// render their report while signalling a non-zero exit code.
    /// </summary>
    public static int Render<T>(MdlResult<T> result, string format, Action<T> renderHuman)
    {
        if (result.Data is null)
        {
            foreach (var diagnostic in result.Diagnostics)
                Console.Error.WriteLine(diagnostic.Message);

            return result.ExitCode == 0 ? 1 : result.ExitCode;
        }

        if (format == OutputFormats.Json)
            JsonOutput.Write(result.Data);
        else
            renderHuman(result.Data);

        return result.ExitCode;
    }
}
