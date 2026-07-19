using System.CommandLine;

namespace Tomix.Cli.Commands;

/// <summary>
/// A self-contained CLI command. Each module owns its options, arguments, handler invocation,
/// and renderer selection, keeping <c>Program</c> a thin registry. Complex output belongs in
/// <c>Output/</c>; command-local rendering is limited to prompts and trivial messages.
/// </summary>
internal interface ICommandModule
{
    Command Build();
}
