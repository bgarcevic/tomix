using System.CommandLine;

namespace Tomix.Cli.Commands;

/// <summary>
/// A self-contained CLI command. Each module owns its options, arguments, and rendering,
/// keeping <c>Program</c> a thin registry that simply adds every module to the root command.
/// </summary>
internal interface ICommandModule
{
    Command Build();
}
