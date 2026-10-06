using System.Runtime.InteropServices;

namespace Tomix.Cli.Serve;

/// <summary>
/// Ctrl+C and termination for the commands that hold a session (<c>tx serve</c>, <c>tx ui</c>).
/// <c>Program.TerminationTimeout</c> turns System.CommandLine's handling off for them, so the
/// process is not ended under them: they decide what a signal means and close the session first.
/// </summary>
internal sealed class ConsoleSignals : IDisposable
{
    /// <summary>The exit code after Ctrl+C (SIGINT), as shells report it.</summary>
    public const int InterruptExitCode = 130;

    /// <summary>The exit code after SIGTERM.</summary>
    public const int TerminateExitCode = 143;

    private readonly PosixSignalRegistration[] _registrations;

    private ConsoleSignals(PosixSignalRegistration[] registrations)
    {
        _registrations = registrations;
    }

    /// <summary>Calls <paramref name="handle"/> on Ctrl+C (SIGINT) and SIGTERM instead of ending the process.</summary>
    public static ConsoleSignals Install(Action<PosixSignal> handle)
    {
        void OnSignal(PosixSignalContext context)
        {
            context.Cancel = true;
            handle(context.Signal);
        }

        return new ConsoleSignals(
        [
            PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal),
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal)
        ]);
    }

    /// <summary>The exit code a process ends with after <paramref name="signal"/>.</summary>
    public static int ExitCode(PosixSignal signal) => signal == PosixSignal.SIGTERM ? TerminateExitCode : InterruptExitCode;

    public void Dispose()
    {
        foreach (var registration in _registrations)
            registration.Dispose();
    }
}
