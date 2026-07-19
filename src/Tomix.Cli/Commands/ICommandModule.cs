using System.CommandLine;

namespace Tomix.Cli.Commands;

/// <summary>
/// A self-contained CLI command and feature-level composition root. Each module owns its options,
/// arguments, application-handler construction and invocation, and renderer selection, keeping
/// <c>Program</c> focused on process-wide dependencies and module registration. Complex output
/// belongs in <c>Output/</c>; command-local rendering is limited to prompts and trivial messages.
/// </summary>
internal interface ICommandModule
{
    Command Build();
}
